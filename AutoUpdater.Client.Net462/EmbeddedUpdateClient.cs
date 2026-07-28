using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AutoUpdater.Client.Net462
{
    public enum UpdateDecision
    {
        InstallNow,
        Postpone
    }

    public sealed class UpdateCommandContext
    {
        public Guid RequestId { get; internal set; }
        public string ControllerIp { get; internal set; }
        public string UpdatePath { get; internal set; }
    }

    public sealed class RollbackCommandContext
    {
        public Guid RequestId { get; internal set; }
        public string ControllerIp { get; internal set; }
        public string TargetVersion { get; internal set; }
    }

    public sealed class EmbeddedClientOptions
    {
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public string CurrentVersion { get; set; }
        public int Port { get; set; } = 45678;
        public string InstallationDirectory { get; set; }
        public string RestartExecutablePath { get; set; } = "卷绕机.exe";
        public string UpdaterExecutablePath { get; set; }
        public string DatabaseConfigFileName { get; set; } =
            "卷绕机.exe.config";
        public string DatabaseConnectionString { get; set; }
    }

    public sealed class EmbeddedUpdateClient : IDisposable
    {
        public const int DefaultPort = 45678;
        private readonly EmbeddedClientOptions _options;
        private readonly ProcessedRequestStore _processedRequests;
        private readonly object _inflightGate = new object();
        private readonly HashSet<Guid> _inflight = new HashSet<Guid>();
        private readonly SynchronizationContext _uiContext;
        private UdpClient _udp;
        private volatile bool _disposed;

        public EmbeddedUpdateClient(EmbeddedClientOptions options)
        {
            if (options == null)
                throw new ArgumentNullException("options");
            if (string.IsNullOrWhiteSpace(options.DeviceId))
                throw new ArgumentException("DeviceId不能为空。");
            _options = options;
            _options.InstallationDirectory = Path.GetFullPath(
                options.InstallationDirectory ??
                AppDomain.CurrentDomain.BaseDirectory);
            _processedRequests = new ProcessedRequestStore(
                _options.InstallationDirectory);
            _uiContext = SynchronizationContext.Current;
        }

        public event Func<UpdateCommandContext, UpdateDecision>
            UpdateDecisionRequired;
        public event Func<RollbackCommandContext, UpdateDecision>
            RollbackDecisionRequired;
        public event Action ShutdownRequested;
        public event Action<Exception> Error;

        public void Start()
        {
            if (_udp != null) return;
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                true);
            _udp.Client.Bind(new IPEndPoint(
                IPAddress.Any, _options.Port));
            Task.Run((Func<Task>)ReceiveLoopAsync);
        }

        private async Task ReceiveLoopAsync()
        {
            while (!_disposed)
            {
                try
                {
                    var result = await _udp.ReceiveAsync()
                        .ConfigureAwait(false);
                    UdpPacket packet;
                    if (!UdpProtocolV1.TryDecode(
                            result.Buffer, out packet))
                        continue;
                    if (packet.Command == UdpCommand.DiscoverRequest)
                        await ReplyDiscoveryAsync(
                            packet.RequestId,
                            result.RemoteEndPoint).ConfigureAwait(false);
                    else
                        _ = Task.Run(() =>
                            HandlePacketAsync(packet, result.RemoteEndPoint));
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    if (_disposed) return;
                }
                catch (Exception ex)
                {
                    RaiseError(ex);
                }
            }
        }

        private Task HandlePacketAsync(
            UdpPacket packet, IPEndPoint controller)
        {
            if (packet.Command == UdpCommand.UpdateRequest)
                return HandleUpdateAsync(packet, controller);
            if (packet.Command == UdpCommand.RollbackRequest)
                return HandleRollbackAsync(packet, controller);
            if (packet.Command == UdpCommand.DatabaseSyncFileRequest)
                return HandleDatabaseFileAsync(packet, controller);
            return Task.CompletedTask;
        }

        private Task ReplyDiscoveryAsync(
            Guid requestId, IPEndPoint controller)
        {
            return SendAsync(
                UdpCommand.DiscoverResponse,
                requestId,
                new DiscoverResponsePayload
                {
                    DeviceId = _options.DeviceId,
                    Name = _options.DeviceName,
                    IpAddress = GetPreferredIpv4(),
                    Version = _options.CurrentVersion,
                    ListenPort = _options.Port
                },
                controller);
        }

        private async Task HandleUpdateAsync(
            UdpPacket packet, IPEndPoint controller)
        {
            var request =
                UdpProtocolV1.DecodePayload<UpdateRequestPayload>(packet);
            if (!IsTarget(request == null
                    ? null
                    : request.TargetDeviceId))
                return;
            await SendReceivedAsync(packet.RequestId, controller)
                .ConfigureAwait(false);
            ProcessedRequest processed;
            if (_processedRequests.TryGet(packet.RequestId, out processed))
            {
                await SendAcceptedAsync(
                    packet.RequestId,
                    controller,
                    processed.Success,
                    processed.Message).ConfigureAwait(false);
                return;
            }
            if (!TryBegin(packet.RequestId))
                return;

            try
            {
                var context = new UpdateCommandContext
                {
                    RequestId = packet.RequestId,
                    ControllerIp = controller.Address.ToString(),
                    UpdatePath = request.UpdatePath
                };
                var accepted = GetUpdateDecision(context) ==
                               UpdateDecision.InstallNow;
                var message = accepted
                    ? "设备已接受更新"
                    : "用户选择稍后更新";
                SaveProcessed(
                    packet.RequestId, "Update", accepted, message, 0);
                await SendAcceptedAsync(
                    packet.RequestId,
                    controller,
                    accepted,
                    message).ConfigureAwait(false);
                if (!accepted) return;

                StartUpdater(BuildUpdaterArguments(
                    packet.RequestId,
                    controller,
                    request.UpdatePath,
                    false,
                    null));
                RaiseOnUi(ShutdownRequested);
            }
            catch (Exception ex)
            {
                await SendUpdateResultAsync(
                    packet.RequestId,
                    controller,
                    false,
                    ex.Message).ConfigureAwait(false);
                RaiseError(ex);
            }
            finally
            {
                End(packet.RequestId);
            }
        }

        private async Task HandleRollbackAsync(
            UdpPacket packet, IPEndPoint controller)
        {
            var request =
                UdpProtocolV1.DecodePayload<RollbackRequestPayload>(packet);
            if (!IsTarget(request == null
                    ? null
                    : request.TargetDeviceId))
                return;
            await SendReceivedAsync(packet.RequestId, controller)
                .ConfigureAwait(false);
            ProcessedRequest processed;
            if (_processedRequests.TryGet(packet.RequestId, out processed))
            {
                await SendAcceptedAsync(
                    packet.RequestId,
                    controller,
                    processed.Success,
                    processed.Message).ConfigureAwait(false);
                return;
            }
            if (!TryBegin(packet.RequestId))
                return;

            try
            {
                var context = new RollbackCommandContext
                {
                    RequestId = packet.RequestId,
                    ControllerIp = controller.Address.ToString(),
                    TargetVersion = request.TargetVersion
                };
                var accepted = GetRollbackDecision(context) ==
                               UpdateDecision.InstallNow;
                var message = accepted
                    ? "设备已接受版本回退"
                    : "用户选择稍后处理";
                SaveProcessed(
                    packet.RequestId, "Rollback", accepted, message, 0);
                await SendAcceptedAsync(
                    packet.RequestId,
                    controller,
                    accepted,
                    message).ConfigureAwait(false);
                if (!accepted) return;

                StartUpdater(BuildUpdaterArguments(
                    packet.RequestId,
                    controller,
                    null,
                    true,
                    request.TargetVersion));
                RaiseOnUi(ShutdownRequested);
            }
            catch (Exception ex)
            {
                await SendUpdateResultAsync(
                    packet.RequestId,
                    controller,
                    false,
                    ex.Message).ConfigureAwait(false);
                RaiseError(ex);
            }
            finally
            {
                End(packet.RequestId);
            }
        }

        private async Task HandleDatabaseFileAsync(
            UdpPacket packet, IPEndPoint controller)
        {
            var request =
                UdpProtocolV1.DecodePayload<
                    DatabaseSyncFileRequestPayload>(packet);
            if (!IsTarget(request == null
                    ? null
                    : request.TargetDeviceId))
                return;
            await SendReceivedAsync(packet.RequestId, controller)
                .ConfigureAwait(false);
            ProcessedRequest processed;
            if (_processedRequests.TryGet(packet.RequestId, out processed))
            {
                await SendDatabaseResultAsync(
                    packet.RequestId,
                    controller,
                    processed.Success,
                    processed.Message,
                    processed.AcceptedChanges).ConfigureAwait(false);
                return;
            }
            if (!TryBegin(packet.RequestId))
                return;

            try
            {
                var package =
                    DatabaseSyncService.LoadAndVerifyPackage(request);
                var connectionString =
                    _options.DatabaseConnectionString;
                if (string.IsNullOrWhiteSpace(connectionString))
                    connectionString =
                        WindingMachineConfigReader.ReadConnectionString(
                            _options.InstallationDirectory,
                            _options.DatabaseConfigFileName);
                var affected = DatabaseSyncService.Execute(
                    connectionString, package);
                var message =
                    "数据库同步成功：" +
                    package.Changes.Count +
                    " 条变更，影响 " + affected + " 行";
                SaveProcessed(
                    packet.RequestId,
                    "DatabaseSync",
                    true,
                    message,
                    package.Changes.Count);
                await SendDatabaseResultAsync(
                    packet.RequestId,
                    controller,
                    true,
                    message,
                    package.Changes.Count).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var message =
                    "数据库同步失败，事务已回滚：" + ex.Message;
                SaveProcessed(
                    packet.RequestId,
                    "DatabaseSync",
                    false,
                    message,
                    0);
                await SendDatabaseResultAsync(
                    packet.RequestId,
                    controller,
                    false,
                    message,
                    0).ConfigureAwait(false);
                RaiseError(ex);
            }
            finally
            {
                End(packet.RequestId);
            }
        }

        private UpdateDecision GetUpdateDecision(
            UpdateCommandContext context)
        {
            UpdateDecision result = UpdateDecision.Postpone;
            InvokeOnUi(() =>
            {
                if (UpdateDecisionRequired != null)
                    result = UpdateDecisionRequired(context);
                else
                    result = MessageBox.Show(
                        "收到软件更新任务：\r\n" +
                        context.UpdatePath +
                        "\r\n\r\n是否立即更新？",
                        "软件更新",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question) == DialogResult.Yes
                        ? UpdateDecision.InstallNow
                        : UpdateDecision.Postpone;
            });
            return result;
        }

        private UpdateDecision GetRollbackDecision(
            RollbackCommandContext context)
        {
            UpdateDecision result = UpdateDecision.Postpone;
            InvokeOnUi(() =>
            {
                if (RollbackDecisionRequired != null)
                    result = RollbackDecisionRequired(context);
                else
                    result = MessageBox.Show(
                        "是否回退到版本：" +
                        (context.TargetVersion ?? "最近备份") + "？",
                        "版本回退",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning) == DialogResult.Yes
                        ? UpdateDecision.InstallNow
                        : UpdateDecision.Postpone;
            });
            return result;
        }

        private void StartUpdater(string arguments)
        {
            var updaterPath = _options.UpdaterExecutablePath;
            if (string.IsNullOrWhiteSpace(updaterPath))
                updaterPath = Path.Combine(
                    _options.InstallationDirectory,
                    "AutoUpdater",
                    "AutoUpdater.Updater.exe");
            if (!File.Exists(updaterPath))
                throw new FileNotFoundException(
                    "找不到独立更新程序。", updaterPath);
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = updaterPath,
                Arguments = arguments,
                UseShellExecute = true,
                WorkingDirectory = _options.InstallationDirectory
            });
            if (process == null)
                throw new InvalidOperationException("更新程序启动失败。");
        }

        private string BuildUpdaterArguments(
            Guid requestId,
            IPEndPoint controller,
            string source,
            bool rollback,
            string targetVersion)
        {
            var parts = new List<string>();
            if (rollback) parts.Add("--rollback");
            if (!string.IsNullOrWhiteSpace(source))
            {
                parts.Add("--source");
                parts.Add(QuoteArgument(source));
            }
            parts.Add("--target");
            parts.Add(QuoteArgument(
                _options.InstallationDirectory.TrimEnd('\\', '/')));
            parts.Add("--process-id");
            parts.Add(Process.GetCurrentProcess().Id.ToString());
            parts.Add("--restart");
            parts.Add(QuoteArgument(
                _options.RestartExecutablePath ?? "卷绕机.exe"));
            parts.Add("--request-id");
            parts.Add(requestId.ToString("N"));
            parts.Add("--device-id");
            parts.Add(QuoteArgument(_options.DeviceId));
            parts.Add("--controller-ip");
            parts.Add(controller.Address.ToString());
            parts.Add("--controller-port");
            parts.Add(controller.Port.ToString());
            if (!string.IsNullOrWhiteSpace(targetVersion))
            {
                parts.Add("--target-version");
                parts.Add(QuoteArgument(targetVersion));
            }
            return string.Join(" ", parts);
        }

        private static string QuoteArgument(string value)
        {
            if (value == null) return "\"\"";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private Task SendReceivedAsync(
            Guid requestId, IPEndPoint target)
        {
            return SendAsync(
                UdpCommand.TaskReceived,
                requestId,
                new TaskReceivedPayload
                {
                    DeviceId = _options.DeviceId
                },
                target);
        }

        private Task SendAcceptedAsync(
            Guid requestId,
            IPEndPoint target,
            bool accepted,
            string message)
        {
            return SendAsync(
                UdpCommand.UpdateAccepted,
                requestId,
                new UpdateAcceptedPayload
                {
                    DeviceId = _options.DeviceId,
                    Accepted = accepted,
                    Message = message
                },
                target);
        }

        private Task SendUpdateResultAsync(
            Guid requestId,
            IPEndPoint target,
            bool success,
            string message)
        {
            return SendAsync(
                UdpCommand.UpdateResult,
                requestId,
                new UpdateResultPayload
                {
                    DeviceId = _options.DeviceId,
                    Success = success,
                    Message = message,
                    CurrentVersion = _options.CurrentVersion
                },
                target);
        }

        private Task SendDatabaseResultAsync(
            Guid requestId,
            IPEndPoint target,
            bool success,
            string message,
            int acceptedChanges)
        {
            return SendAsync(
                UdpCommand.DatabaseSyncResult,
                requestId,
                new DatabaseSyncResultPayload
                {
                    DeviceId = _options.DeviceId,
                    Success = success,
                    Message = message,
                    AcceptedChanges = acceptedChanges
                },
                target);
        }

        private Task SendAsync(
            UdpCommand command,
            Guid requestId,
            object payload,
            IPEndPoint target)
        {
            var bytes = UdpProtocolV1.Encode(
                command, requestId, payload);
            return _udp.SendAsync(bytes, bytes.Length, target);
        }

        private bool IsTarget(string targetDeviceId)
        {
            return string.Equals(
                targetDeviceId,
                _options.DeviceId,
                StringComparison.OrdinalIgnoreCase);
        }

        private bool TryBegin(Guid requestId)
        {
            lock (_inflightGate)
                return _inflight.Add(requestId);
        }

        private void End(Guid requestId)
        {
            lock (_inflightGate)
                _inflight.Remove(requestId);
        }

        private void SaveProcessed(
            Guid requestId,
            string operation,
            bool success,
            string message,
            int acceptedChanges)
        {
            try
            {
                _processedRequests.Save(new ProcessedRequest
                {
                    RequestId = requestId,
                    Operation = operation,
                    Success = success,
                    Message = message,
                    ProcessedAt = DateTime.UtcNow,
                    AcceptedChanges = acceptedChanges
                });
            }
            catch (Exception ex)
            {
                RaiseError(ex);
            }
        }

        private void InvokeOnUi(Action action)
        {
            if (_uiContext == null ||
                SynchronizationContext.Current == _uiContext)
                action();
            else
                _uiContext.Send(state => action(), null);
        }

        private void RaiseOnUi(Action handler)
        {
            if (handler == null) return;
            if (_uiContext == null)
                handler();
            else
                _uiContext.Post(state => handler(), null);
        }

        private void RaiseError(Exception exception)
        {
            var handler = Error;
            if (handler == null) return;
            if (_uiContext == null)
                handler(exception);
            else
                _uiContext.Post(state => handler(exception), null);
        }

        private static string GetPreferredIpv4()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(item =>
                    item.OperationalStatus == OperationalStatus.Up &&
                    item.NetworkInterfaceType !=
                    NetworkInterfaceType.Loopback)
                .SelectMany(item =>
                    item.GetIPProperties().UnicastAddresses)
                .Where(item =>
                    item.Address.AddressFamily ==
                    AddressFamily.InterNetwork)
                .Select(item => item.Address.ToString())
                .FirstOrDefault() ?? "0.0.0.0";
        }

        public void Dispose()
        {
            _disposed = true;
            if (_udp != null)
            {
                _udp.Close();
                _udp = null;
            }
        }
    }
}
