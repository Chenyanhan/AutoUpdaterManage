using System;
using System.Drawing;
using System.Windows.Forms;

namespace AutoUpdater.Client.Net462
{
    internal static class DesktopUpdatePrompt
    {
        public static UpdateDecision ShowUpdate(
            UpdateCommandContext context,
            string deviceName)
        {
            return ShowDialog(
                "发现软件更新",
                deviceName + " 收到管理端下发的软件更新指令。",
                "更新来源：" + context.UpdatePath +
                "\r\n\r\n立即更新会关闭当前上位机，安装完成后自动重新启动。",
                "立即更新",
                "稍后更新");
        }

        public static UpdateDecision ShowRollback(
            RollbackCommandContext context,
            string deviceName)
        {
            return ShowDialog(
                "确认版本回退",
                deviceName + " 收到管理端下发的版本回退指令。",
                "目标版本：" +
                (context.TargetVersion ?? "最近一次备份") +
                "\r\n\r\n立即回退会关闭当前上位机，完成后自动重新启动。",
                "立即回退",
                "稍后处理");
        }

        private static UpdateDecision ShowDialog(
            string title,
            string heading,
            string detail,
            string acceptText,
            string postponeText)
        {
            using (var dialog = new Form())
            using (var root = new TableLayoutPanel())
            using (var headingLabel = new Label())
            using (var detailLabel = new Label())
            using (var buttonPanel = new FlowLayoutPanel())
            using (var postponeButton = CreateButton(
                       postponeText,
                       Color.FromArgb(232, 237, 245),
                       Color.FromArgb(39, 54, 79)))
            using (var acceptButton = CreateButton(
                       acceptText,
                       Color.FromArgb(37, 99, 235),
                       Color.White))
            {
                dialog.Text = title;
                dialog.StartPosition = FormStartPosition.CenterScreen;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.ClientSize = new Size(520, 272);
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ShowInTaskbar = true;
                dialog.TopMost = true;
                dialog.BackColor = Color.White;
                dialog.Font = new Font(
                    "Microsoft YaHei UI", 9F, FontStyle.Regular);

                root.Dock = DockStyle.Fill;
                root.Padding = new Padding(28, 26, 28, 24);
                root.ColumnCount = 1;
                root.RowCount = 3;
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));

                headingLabel.AutoSize = true;
                headingLabel.Dock = DockStyle.Fill;
                headingLabel.Font = new Font(
                    "Microsoft YaHei UI", 13.5F, FontStyle.Bold);
                headingLabel.ForeColor = Color.FromArgb(23, 32, 51);
                headingLabel.Text = heading;
                headingLabel.Margin = Padding.Empty;

                detailLabel.AutoSize = false;
                detailLabel.Dock = DockStyle.Fill;
                detailLabel.Font = new Font(
                    "Microsoft YaHei UI", 9.5F, FontStyle.Regular);
                detailLabel.ForeColor = Color.FromArgb(89, 103, 128);
                detailLabel.Text = detail;
                detailLabel.Padding = new Padding(0, 18, 0, 10);
                detailLabel.Margin = Padding.Empty;

                buttonPanel.Dock = DockStyle.Fill;
                buttonPanel.FlowDirection = FlowDirection.RightToLeft;
                buttonPanel.WrapContents = false;
                buttonPanel.Padding = Padding.Empty;
                buttonPanel.Margin = Padding.Empty;

                acceptButton.Margin = new Padding(12, 0, 0, 0);
                postponeButton.Margin = Padding.Empty;
                acceptButton.DialogResult = DialogResult.OK;
                postponeButton.DialogResult = DialogResult.Cancel;
                buttonPanel.Controls.Add(acceptButton);
                buttonPanel.Controls.Add(postponeButton);

                root.Controls.Add(headingLabel, 0, 0);
                root.Controls.Add(detailLabel, 0, 1);
                root.Controls.Add(buttonPanel, 0, 2);
                dialog.Controls.Add(root);
                dialog.AcceptButton = acceptButton;
                dialog.CancelButton = postponeButton;

                return dialog.ShowDialog() == DialogResult.OK
                    ? UpdateDecision.InstallNow
                    : UpdateDecision.Postpone;
            }
        }

        private static Button CreateButton(
            string text,
            Color background,
            Color foreground)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = false,
                Size = new Size(106, 38),
                BackColor = background,
                ForeColor = foreground,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
                Font = new Font(
                    "Microsoft YaHei UI", 9F, FontStyle.Regular)
            };
            button.FlatAppearance.BorderSize = 0;
            return button;
        }
    }
}
