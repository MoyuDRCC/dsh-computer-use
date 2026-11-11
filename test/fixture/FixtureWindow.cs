// 受控测试窗口：覆盖电脑操作需要处理的各类控件与交互。
//
// 每个动作都会把结果写进顶部的状态标签，测试脚本用 observe 读这个标签来断言，
// 因此「操作真的生效了」与「事件只是发出去了」能被区分开。
//
// 编译：csc /target:winexe /out:FixtureWindow.exe FixtureWindow.cs
//       /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DshCuaFixture
{
    internal sealed class FixtureForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private readonly Label status;
        private readonly TextBox input;
        private int buttonClicks;

        public FixtureForm()
        {
            Text = "DSH CUA Fixture";
            ClientSize = new Size(900, 470);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(80, 80);
            KeyPreview = true;

            // ── 顶部菜单 ───────────────────────────────────────
            var strip = new MenuStrip();
            strip.Name = "menuStrip";
            var fileMenu = new ToolStripMenuItem("文件");
            fileMenu.Name = "menuFile";
            var newItem = new ToolStripMenuItem("新建");
            newItem.Name = "menuNew";
            newItem.Click += delegate { Report("menu:new"); };
            var openItem = new ToolStripMenuItem("打开");
            openItem.Name = "menuOpen";
            openItem.Click += delegate { Report("menu:open"); };
            fileMenu.DropDownItems.Add(newItem);
            fileMenu.DropDownItems.Add(openItem);
            strip.Items.Add(fileMenu);
            MainMenuStrip = strip;
            Controls.Add(strip);

            // ── 状态显示（断言点）────────────────────────────
            status = new Label();
            status.Name = "lblStatus";
            status.Text = "idle";
            status.BorderStyle = BorderStyle.FixedSingle;
            status.Bounds = new Rectangle(12, 32, 876, 22);
            Controls.Add(status);

            // ── 文本输入 ──────────────────────────────────────
            input = new TextBox();
            input.Name = "txtInput";
            input.Text = "initial";
            input.Bounds = new Rectangle(12, 64, 380, 24);
            input.TextChanged += delegate { Report("text:" + input.Text); };
            Controls.Add(input);

            var button = new Button();
            button.Name = "btnSubmit";
            button.Text = "提交";
            button.Bounds = new Rectangle(400, 62, 90, 28);
            button.Click += delegate { buttonClicks++; Report("button:" + buttonClicks + ":" + input.Text); };
            Controls.Add(button);

            var check = new CheckBox();
            check.Name = "chkOption";
            check.Text = "勾选项";
            check.Bounds = new Rectangle(500, 64, 120, 24);
            check.CheckedChanged += delegate { Report("check:" + check.Checked); };
            Controls.Add(check);

            var radioA = new RadioButton();
            radioA.Name = "rdoA";
            radioA.Text = "甲";
            radioA.Bounds = new Rectangle(640, 64, 70, 24);
            radioA.CheckedChanged += delegate { if (radioA.Checked) Report("radio:A"); };
            Controls.Add(radioA);

            var radioB = new RadioButton();
            radioB.Name = "rdoB";
            radioB.Text = "乙";
            radioB.Bounds = new Rectangle(715, 64, 70, 24);
            radioB.CheckedChanged += delegate { if (radioB.Checked) Report("radio:B"); };
            Controls.Add(radioB);

            // ── 选择类 ────────────────────────────────────────
            var combo = new ComboBox();
            combo.Name = "cboChoice";
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.Bounds = new Rectangle(12, 100, 160, 24);
            combo.Items.AddRange(new object[] { "甲选项", "乙选项", "丙选项" });
            combo.SelectedIndexChanged += delegate { Report("combo:" + combo.SelectedIndex); };
            Controls.Add(combo);

            var number = new NumericUpDown();
            number.Name = "numCount";
            number.Bounds = new Rectangle(190, 100, 90, 24);
            number.Minimum = 0;
            number.Maximum = 100;
            number.Value = 10;
            number.ValueChanged += delegate { Report("number:" + number.Value); };
            Controls.Add(number);

            var track = new TrackBar();
            track.Name = "trkLevel";
            track.Bounds = new Rectangle(300, 92, 240, 40);
            track.Minimum = 0;
            track.Maximum = 100;
            track.Value = 20;
            track.ValueChanged += delegate { Report("track:" + track.Value); };
            Controls.Add(track);

            var list = new ListBox();
            list.Name = "lstItems";
            list.Bounds = new Rectangle(12, 142, 220, 190);
            for (int i = 1; i <= 20; i++) list.Items.Add("项目 " + i);
            list.SelectedIndexChanged += delegate { Report("list:" + list.SelectedIndex); };
            list.DoubleClick += delegate { Report("list-double:" + list.SelectedIndex); };
            Controls.Add(list);

            var tree = new TreeView();
            tree.Name = "treeNodes";
            tree.Bounds = new Rectangle(245, 142, 215, 190);
            var root = tree.Nodes.Add("根节点");
            root.Nodes.Add("子节点 A");
            root.Nodes.Add("子节点 B");
            // WinForms 的 TreeNode 只有展开后才在无障碍树里出现，先展开才能被测到
            root.Expand();
            tree.AfterSelect += delegate { Report("tree:" + tree.SelectedNode.Text); };
            Controls.Add(tree);

            var tabs = new TabControl();
            tabs.Name = "tabs";
            tabs.Bounds = new Rectangle(472, 142, 416, 190);
            var page1 = new TabPage("第一页");
            page1.Name = "pageOne";
            var page2 = new TabPage("第二页");
            page2.Name = "pageTwo";
            var pageLabel = new Label();
            pageLabel.Name = "lblPageTwo";
            pageLabel.Text = "第二页内容";
            pageLabel.Bounds = new Rectangle(14, 14, 200, 24);
            page2.Controls.Add(pageLabel);
            tabs.TabPages.Add(page1);
            tabs.TabPages.Add(page2);
            tabs.SelectedIndexChanged += delegate { Report("tab:" + tabs.SelectedIndex); };
            Controls.Add(tabs);

            // ── 只能坐标操作的自绘区域 ────────────────────────
            var canvas = new Panel();
            canvas.Name = "canvas";
            canvas.Bounds = new Rectangle(12, 348, 220, 80);
            canvas.BorderStyle = BorderStyle.FixedSingle;
            canvas.Paint += delegate(object sender, PaintEventArgs e)
            {
                e.Graphics.FillRectangle(Brushes.SteelBlue, 0, 0, canvas.Width, canvas.Height);
                e.Graphics.DrawString("自绘区域（无 UIA 语义）", SystemFonts.DefaultFont, Brushes.White, 10, 30);
            };
            canvas.MouseClick += delegate(object sender, MouseEventArgs e) { Report("canvas:" + e.Button + ":" + e.Clicks); };
            canvas.MouseDoubleClick += delegate(object sender, MouseEventArgs e) { Report("canvas:double:" + e.Clicks); };
            Controls.Add(canvas);

            // ── 拖拽源与目标 ──────────────────────────────────
            var source = new Label();
            source.Name = "dragSource";
            source.Text = "拖我";
            source.TextAlign = ContentAlignment.MiddleCenter;
            source.BorderStyle = BorderStyle.FixedSingle;
            source.Bounds = new Rectangle(245, 348, 100, 40);
            source.MouseDown += delegate { source.DoDragDrop("payload", DragDropEffects.Copy); };
            Controls.Add(source);

            var target = new Label();
            target.Name = "dropTarget";
            target.Text = "放这里";
            target.TextAlign = ContentAlignment.MiddleCenter;
            target.BorderStyle = BorderStyle.FixedSingle;
            target.AllowDrop = true;
            target.Bounds = new Rectangle(360, 348, 100, 40);
            target.DragEnter += delegate(object sender, DragEventArgs e) { e.Effect = DragDropEffects.Copy; };
            target.DragDrop += delegate { Report("drop:ok"); };
            Controls.Add(target);

            // ── 右键菜单 ──────────────────────────────────────
            var menu = new ContextMenuStrip();
            menu.Name = "ctxMenu";
            var item1 = new ToolStripMenuItem("菜单项一");
            item1.Name = "ctxItem1";
            item1.Click += delegate { Report("context:1"); };
            var item2 = new ToolStripMenuItem("菜单项二");
            item2.Name = "ctxItem2";
            item2.Click += delegate { Report("context:2"); };
            menu.Items.Add(item1);
            menu.Items.Add(item2);
            ContextMenuStrip = menu;

            // 快捷键：验证 key 动作能触发真实行为
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.S) Report("hotkey:ctrl+s");
            };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 自己创建的窗口有前台权限，趁这一刻把焦点拿回来，测试才能覆盖键盘类动作
            Activate();
            SetForegroundWindow(Handle);
        }

        private void Report(string text)
        {
            status.Text = text;
            Text = "DSH CUA Fixture | " + text;
        }

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.Run(new FixtureForm());
        }
    }
}
