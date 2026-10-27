using System;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ArduinoStudio7
{
    // Главният прозорец — оформление в стил Visual Studio с проект, редактор и изход.
    public sealed class MainForm : Form
    {
        private readonly CodeEditor _editor = new CodeEditor();
        private readonly TreeView _projectTree = new TreeView();
        private readonly RichTextBox _output = new RichTextBox();
        private readonly ComboBox _board = new ComboBox();
        private readonly ComboBox _port = new ComboBox();
        private readonly ArduinoToolchain _toolchain = new ArduinoToolchain();
        private string _currentFile = "";
        private string _projectFolder = "";
        private readonly SerialPort _serial = new SerialPort();

        public MainForm()
        {
            // Задаваме основните параметри на прозореца.
            Text = "Arduino Studio 7";
            Width = 1200; Height = 780;
            MinimumSize = new Size(850, 550);
            StartPosition = FormStartPosition.CenterScreen;
            // Стартираме максимизиран, за да има достатъчно място за редактора и кода.
            WindowState = FormWindowState.Maximized;
            BackColor = Color.FromArgb(37, 37, 38);
            ForeColor = Color.Gainsboro;
            BuildInterface();
            NewSketch();
            RefreshPorts();
        }

        private void BuildInterface()
        {
            // Главното меню е подредено подобно на Visual Studio.
            var menu = new MenuStrip { BackColor = Color.FromArgb(245, 245, 245), ForeColor = Color.FromArgb(30, 30, 30), RenderMode = ToolStripRenderMode.System };
            var fileMenu = new ToolStripMenuItem("Файл");
            fileMenu.DropDownItems.Add("Нов скеч", null, (s, e) => NewSketch());
            fileMenu.DropDownItems.Add("Отвори...", null, (s, e) => OpenFile());
            fileMenu.DropDownItems.Add("Запиши", null, (s, e) => SaveFile());
            fileMenu.DropDownItems.Add("Запиши като...", null, (s, e) => SaveFileAs());
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("Отвори папка проект...", null, (s, e) => OpenProjectFolder());
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("Изход", null, (s, e) => Close());
            var buildMenu = new ToolStripMenuItem("Скица");
            buildMenu.DropDownItems.Add("Провери / Компилирай", null, (s, e) => CompileSketch());
            buildMenu.DropDownItems.Add("Качи към платката", null, (s, e) => UploadSketch());
            buildMenu.DropDownItems.Add("Обнови COM портове", null, (s, e) => RefreshPorts());
            var toolsMenu = new ToolStripMenuItem("Инструменти");
            toolsMenu.DropDownItems.Add("Настройки на Arduino CLI...", null, (s, e) => ChooseCli());
            toolsMenu.DropDownItems.Add("Справка за платките...", null, (s, e) => ShowBoardHelp());
            // Меню за редактиране, търсене и навигация в кода.
            var editMenu = new ToolStripMenuItem("Редактиране");
            editMenu.DropDownItems.Add("Намери следващо...   Ctrl+F", null, (s, e) => FindInEditor());
            editMenu.DropDownItems.Add("Замени всички...", null, (s, e) => ReplaceInEditor());
            editMenu.DropDownItems.Add("Отиди на ред...   Ctrl+G", null, (s, e) => GoToEditorLine());
            editMenu.DropDownItems.Add(new ToolStripSeparator());
            editMenu.DropDownItems.Add("Коментирай / разкоментирай   Ctrl+/", null, (s, e) => _editor.ToggleCommentSelection());
            menu.Items.AddRange(new ToolStripItem[] { fileMenu, editMenu, buildMenu, toolsMenu });
            MainMenuStrip = menu;
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                // Клавишните комбинации за редактора работят и когато фокусът е в кода.
                if (e.Control && e.KeyCode == Keys.F) { e.SuppressKeyPress = true; FindInEditor(); }
                else if (e.Control && e.KeyCode == Keys.H) { e.SuppressKeyPress = true; ReplaceInEditor(); }
                else if (e.Control && e.KeyCode == Keys.G) { e.SuppressKeyPress = true; GoToEditorLine(); }
            };

            // Лента с действия, платка и сериен порт.
            var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, BackColor = Color.FromArgb(245, 245, 245), ForeColor = Color.FromArgb(30, 30, 30), RenderMode = ToolStripRenderMode.System };
            toolbar.Items.Add(Button("Нов", (s,e) => NewSketch()));
            toolbar.Items.Add(Button("Отвори", (s,e) => OpenFile()));
            toolbar.Items.Add(Button("Запиши", (s,e) => SaveFile()));
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(Button("Компилирай", (s,e) => CompileSketch()));
            toolbar.Items.Add(Button("Качи", (s,e) => UploadSketch()));
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(new ToolStripLabel("Платка:"));
            _board.Width = 190;
            _board.DropDownStyle = ComboBoxStyle.DropDown;
            _board.Items.AddRange(new object[] { "arduino:avr:mega", "arduino:avr:uno", "arduino:avr:nano", "arduino:megaavr:uno2018" });
            _board.Text = "arduino:avr:mega";
            toolbar.Items.Add(new ToolStripControlHost(_board));
            toolbar.Items.Add(new ToolStripLabel("Порт:"));
            _port.Width = 90;
            _port.DropDownStyle = ComboBoxStyle.DropDownList;
            toolbar.Items.Add(new ToolStripControlHost(_port));
            toolbar.Items.Add(Button("Обнови", (s,e) => RefreshPorts()));

            // Редакторът заема центъра и използва светлата цветова схема на Visual Studio.
            _editor.Dock = DockStyle.Fill;
            _editor.Margin = Padding.Empty;

            // Таб за отворения документ, както в IDE.
            var documents = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 4), HotTrack = true };
            var documentTab = new TabPage("Нов скеч") { BackColor = Color.White, Padding = Padding.Empty };
            documentTab.Controls.Add(_editor);
            documents.TabPages.Add(documentTab);

            // Solution Explorer е вдясно, а панелът Properties е под него.
            _projectTree.Dock = DockStyle.Fill;
            _projectTree.BackColor = Color.White;
            _projectTree.ForeColor = Color.FromArgb(30, 30, 30);
            _projectTree.BorderStyle = BorderStyle.None;
            _projectTree.HideSelection = false;
            _projectTree.NodeMouseDoubleClick += (s,e) => { if (e.Node.Tag is string) LoadFile((string)e.Node.Tag); };

            var projectPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            var projectHeader = SectionLabel("SOLUTION EXPLORER");
            projectHeader.BackColor = Color.FromArgb(235, 235, 235);
            projectHeader.ForeColor = Color.FromArgb(30, 30, 30);
            projectHeader.Dock = DockStyle.Top;
            projectHeader.Height = 27;
            projectPanel.Controls.Add(_projectTree);
            projectPanel.Controls.Add(projectHeader);

            var propertiesList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = Color.FromArgb(30, 30, 30) };
            propertiesList.Columns.Add("Свойство", 115);
            propertiesList.Columns.Add("Стойност", 130);
            propertiesList.Items.Add(new ListViewItem(new[] { "Език", "Arduino / C++" }));
            propertiesList.Items.Add(new ListViewItem(new[] { "Платка", _board.Text }));
            propertiesList.Items.Add(new ListViewItem(new[] { "Редактор", "C++ код" }));
            var propertiesPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            var propertiesHeader = SectionLabel("PROPERTIES");
            propertiesHeader.BackColor = Color.FromArgb(235, 235, 235);
            propertiesHeader.ForeColor = Color.FromArgb(30, 30, 30);
            propertiesHeader.Dock = DockStyle.Top;
            propertiesHeader.Height = 27;
            propertiesPanel.Controls.Add(propertiesList);
            propertiesPanel.Controls.Add(propertiesHeader);

            var rightTools = new SplitContainer
            {
                Size = new Size(300, 700), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
                SplitterDistance = 340, Panel1MinSize = 150, Panel2MinSize = 100,
                FixedPanel = FixedPanel.Panel2, IsSplitterFixed = false,
                BackColor = Color.FromArgb(210, 210, 210), SplitterWidth = 5
            };
            rightTools.Panel1.Controls.Add(projectPanel);
            rightTools.Panel2.Controls.Add(propertiesPanel);

            // Главният разделител дава приблизително 80% от ширината на кода.
            var upper = new SplitContainer
            {
                Size = new Size(1200, 700), Dock = DockStyle.Fill, Orientation = Orientation.Vertical,
                SplitterDistance = 900, Panel1MinSize = 350, Panel2MinSize = 220,
                FixedPanel = FixedPanel.Panel2, IsSplitterFixed = false,
                BackColor = Color.FromArgb(210, 210, 210), SplitterWidth = 5
            };
            upper.Panel1.Controls.Add(documents);
            upper.Panel2.Controls.Add(rightTools);

            // Долният прозорец за грешки и компилация е отделен и може да се преоразмерява.
            _output.Dock = DockStyle.Fill;
            _output.ReadOnly = true;
            _output.BackColor = Color.FromArgb(30, 30, 30);
            _output.ForeColor = Color.FromArgb(230, 230, 230);
            _output.Font = new Font("Consolas", 9.0f);
            _output.BorderStyle = BorderStyle.None;
            var lower = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 30, 30) };
            var outputHeader = SectionLabel("OUTPUT / COMPILATION");
            outputHeader.Dock = DockStyle.Top;
            outputHeader.Height = 25;
            lower.Controls.Add(_output);
            lower.Controls.Add(outputHeader);

            var split = new SplitContainer
            {
                Size = new Size(1200, 700), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
                SplitterDistance = 500, Panel1MinSize = 220, Panel2MinSize = 90,
                FixedPanel = FixedPanel.Panel2, IsSplitterFixed = false,
                BackColor = Color.FromArgb(210, 210, 210), SplitterWidth = 5
            };
            split.Panel1.Controls.Add(upper);
            split.Panel2.Controls.Add(lower);

            // Долна синя статусна лента, вдъхновена от Visual Studio.
            var status = new StatusStrip { BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, SizingGrip = false };
            status.Items.Add(new ToolStripStatusLabel("Arduino Studio 7"));
            status.Items.Add(new ToolStripStatusLabel(" | Arduino / C++ |"));
            status.Items.Add(new ToolStripStatusLabel(" Готово"));

            Controls.Add(split);
            Controls.Add(toolbar);
            Controls.Add(menu);
            Controls.Add(status);
            menu.Dock = DockStyle.Top;
            toolbar.Dock = DockStyle.Top;
            status.Dock = DockStyle.Bottom;

            // Първоначалните пропорции се настройват след като Windows определи реалния размер.
            Shown += (s, e) =>
            {
                if (split.Height > 350)
                    split.SplitterDistance = Math.Max(split.Panel1MinSize, Math.Min((int)(split.Height * 0.78), split.Height - split.Panel2MinSize - split.SplitterWidth));
                if (upper.Width > 650)
                    upper.SplitterDistance = Math.Max(upper.Panel1MinSize, Math.Min((int)(upper.Width * 0.79), upper.Width - upper.Panel2MinSize - upper.SplitterWidth));
                if (rightTools.Height > 300)
                    rightTools.SplitterDistance = Math.Max(rightTools.Panel1MinSize, Math.Min((int)(rightTools.Height * 0.68), rightTools.Height - rightTools.Panel2MinSize - rightTools.SplitterWidth));
            };
        }

        private string AskText(string title, string prompt, string initialValue)
        {
            // Малък диалог без допълнителни библиотеки за търсене и навигация.
            using (var dialog = new Form())
            using (var label = new Label())
            using (var input = new TextBox())
            using (var ok = new Button())
            using (var cancel = new Button())
            {
                dialog.Text = title;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.ClientSize = new Size(390, 112);
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ShowInTaskbar = false;
                label.Text = prompt;
                label.SetBounds(12, 10, 366, 20);
                input.Text = initialValue ?? "";
                input.SetBounds(12, 34, 366, 24);
                ok.Text = "OK";
                ok.DialogResult = DialogResult.OK;
                ok.SetBounds(222, 72, 75, 28);
                cancel.Text = "Отказ";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.SetBounds(303, 72, 75, 28);
                dialog.Controls.AddRange(new Control[] { label, input, ok, cancel });
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                return dialog.ShowDialog(this) == DialogResult.OK ? input.Text : null;
            }
        }

        private void FindInEditor()
        {
            // Търсим текста от текущата позиция и продължаваме от началото.
            string query = AskText("Намери", "Текст за търсене:", "");
            if (query == null) return;
            if (!_editor.FindNext(query))
                MessageBox.Show(this, "Текстът не е намерен.", "Намери", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ReplaceInEditor()
        {
            // Замяна на всички срещания на зададения текст.
            string query = AskText("Замени", "Текст, който да се търси:", "");
            if (String.IsNullOrEmpty(query)) return;
            string replacement = AskText("Замени", "Замени с:", "");
            if (replacement == null) return;
            int count = _editor.ReplaceAllText(query, replacement);
            Log("Замяна: " + count + " срещания.");
        }

        private void GoToEditorLine()
        {
            // Придвижваме редактора до посочен номер на ред.
            string value = AskText("Отиди на ред", "Номер на ред:", "1");
            int line;
            if (value == null) return;
            if (!Int32.TryParse(value, out line) || !_editor.GoToLine(line))
                MessageBox.Show(this, "Въведете валиден номер на ред.", "Отиди на ред", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private ToolStripButton Button(string text, EventHandler action)
        {
            // Общ стил за бутоните в лентата с инструменти.
            var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text };
            b.Click += action; return b;
        }

        private Label SectionLabel(string text)
        {
            // Етикет за заглавията на панелите.
            return new Label { Text = text, Dock = DockStyle.Top, Height = 25, TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.FromArgb(45,45,48), ForeColor = Color.Gainsboro, Padding = new Padding(7,0,0,0) };
        }

        private void NewSketch()
        {
            // Подготвяме нов примерен Arduino скеч.
            _currentFile = ""; _projectFolder = "";
            _editor.Text = "void setup()\n{\n    pinMode(LED_BUILTIN, OUTPUT);\n}\n\nvoid loop()\n{\n    digitalWrite(LED_BUILTIN, HIGH);\n    delay(500);\n    digitalWrite(LED_BUILTIN, LOW);\n    delay(500);\n}\n";
            Text = "Arduino Studio 7 — Нов скеч";
            _projectTree.Nodes.Clear();
            _projectTree.Nodes.Add(new TreeNode("Нов скеч (не е записан)"));
            Log("Готово. Запишете скеча в папка със същото име като .ino файла.");
        }

        private void OpenFile()
        {
            // Отваряме файл с код, избран от потребителя.
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "Arduino/C++ файлове|*.ino;*.h;*.hpp;*.c;*.cpp|Всички файлове|*.*";
                if (dialog.ShowDialog(this) == DialogResult.OK) LoadFile(dialog.FileName);
            }
        }

        private void LoadFile(string path)
        {
            // Зареждаме текстовия файл и обновяваме заглавието/дървото.
            try
            {
                _currentFile = path; _projectFolder = Path.GetDirectoryName(path);
                _editor.Text = File.ReadAllText(path);
                Text = "Arduino Studio 7 — " + Path.GetFileName(path);
                RefreshProjectTree();
                Log("Отворен файл: " + path);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Грешка при отваряне", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void SaveFile()
        {
            // Записваме файла; при нов скеч първо искаме име и местоположение.
            if (String.IsNullOrEmpty(_currentFile)) { SaveFileAs(); return; }
            try { File.WriteAllText(_currentFile, _editor.Text); RefreshProjectTree(); Log("Записан: " + _currentFile); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Грешка при запис", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void SaveFileAs()
        {
            // Записваме като .ino в папка, чието име съвпада с името на скеча.
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "Arduino скеч (*.ino)|*.ino|C++ файл (*.cpp)|*.cpp|Всички файлове|*.*";
                dialog.FileName = "MySketch.ino";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string folder = Path.GetDirectoryName(dialog.FileName);
                    string stem = Path.GetFileNameWithoutExtension(dialog.FileName);
                    string targetFolder = Path.Combine(folder, stem);
                    if (Path.GetExtension(dialog.FileName).Equals(".ino", StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(targetFolder);
                        _currentFile = Path.Combine(targetFolder, Path.GetFileName(dialog.FileName));
                    }
                    else _currentFile = dialog.FileName;
                    _projectFolder = Path.GetDirectoryName(_currentFile);
                    File.WriteAllText(_currentFile, _editor.Text);
                    Text = "Arduino Studio 7 — " + Path.GetFileName(_currentFile);
                    RefreshProjectTree(); Log("Записан: " + _currentFile);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Грешка при запис", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        private void OpenProjectFolder()
        {
            // Зареждаме всички разпознати файлове от избраната папка на проекта.
            using (var dialog = new FolderBrowserDialog())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _projectFolder = dialog.SelectedPath;
                string ino = Directory.GetFiles(_projectFolder, "*.ino", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (ino != null) LoadFile(ino); else RefreshProjectTree();
            }
        }

        private void RefreshProjectTree()
        {
            // Показваме файловете в текущата папка без да влизаме в build/cache папки.
            _projectTree.Nodes.Clear();
            if (String.IsNullOrEmpty(_projectFolder) || !Directory.Exists(_projectFolder))
            {
                if (!String.IsNullOrEmpty(_currentFile)) _projectTree.Nodes.Add(new TreeNode(Path.GetFileName(_currentFile)) { Tag = _currentFile });
                return;
            }
            var root = new TreeNode(Path.GetFileName(_projectFolder)) { Tag = _projectFolder };
            foreach (string file in Directory.GetFiles(_projectFolder, "*.*", SearchOption.AllDirectories)
                .Where(f => Regex.IsMatch(Path.GetExtension(f), @"^\.(ino|h|hpp|c|cpp)$", RegexOptions.IgnoreCase))
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "build" + Path.DirectorySeparatorChar)))
                root.Nodes.Add(new TreeNode(file.Substring(_projectFolder.TrimEnd(Path.DirectorySeparatorChar).Length + 1)) { Tag = file });
            root.Expand(); _projectTree.Nodes.Add(root);
        }

        private void CompileSketch()
        {
            // Записваме промените и подаваме папката на скеча към Arduino CLI.
            SaveFile();
            if (String.IsNullOrEmpty(_currentFile) || !File.Exists(_currentFile)) return;
            string sketchFolder = Path.GetDirectoryName(_currentFile);
            if (!Directory.GetFiles(sketchFolder, "*.ino", SearchOption.TopDirectoryOnly).Any())
            {
                Log("Грешка: Arduino CLI очаква .ino файл в папката на скеча.");
                return;
            }
            RunCli("compile --fqbn " + Quote(_board.Text.Trim()) + " " + Quote(sketchFolder), sketchFolder);
        }

        private void UploadSketch()
        {
            // Качваме само след запис и при избран сериен порт.
            SaveFile();
            if (String.IsNullOrEmpty(_currentFile) || !File.Exists(_currentFile)) return;
            if (String.IsNullOrWhiteSpace(_port.Text)) { Log("Изберете COM порт преди качване."); return; }
            string sketchFolder = Path.GetDirectoryName(_currentFile);
            RunCli("upload --fqbn " + Quote(_board.Text.Trim()) + " --port " + Quote(_port.Text) + " " + Quote(sketchFolder), sketchFolder);
        }

        private void RunCli(string arguments, string workingDirectory)
        {
            // Изпълняваме CLI във фонов режим, за да не блокираме прозореца.
            Log("> arduino-cli " + arguments);
            var worker = new System.ComponentModel.BackgroundWorker();
            worker.DoWork += (s,e) =>
            {
                try { e.Result = _toolchain.Run(arguments, workingDirectory, line => BeginInvoke((Action)(() => Log(line)))); }
                catch (Exception ex) { e.Result = ex.Message; }
            };
            worker.RunWorkerCompleted += (s,e) => Log("Операцията приключи: " + e.Result);
            worker.RunWorkerAsync();
        }

        private string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }

        private void ChooseCli()
        {
            // Позволяваме избор на arduino-cli.exe, включително от преносима папка.
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "Arduino CLI (arduino-cli.exe)|arduino-cli.exe|Изпълними файлове (*.exe)|*.exe";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _toolchain.CliPath = dialog.FileName;
                    Log("Arduino CLI: " + _toolchain.CliPath);
                }
            }
        }

        private void ShowBoardHelp()
        {
            // Показваме кратка помощ за избор на FQBN и добавяне на ядра.
            MessageBox.Show(this,
                "Въведете FQBN на платката, например:\narduino:avr:mega — Mega 2560\narduino:avr:uno — Uno\narduino:avr:nano — Nano\n\nЗа други платки инсталирайте съответния core с Arduino CLI. Пример: arduino-cli core install arduino:avr. За ESP32 следвайте инструкциите на производителя.",
                "Платки и FQBN", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void RefreshPorts()
        {
            // Обновяваме списъка с наличните COM портове.
            string previous = _port.Text;
            _port.Items.Clear();
            try { _port.Items.AddRange(SerialPort.GetPortNames().OrderBy(p => p).Cast<object>().ToArray()); }
            catch (Exception ex) { Log("Не могат да се прочетат COM портовете: " + ex.Message); }
            if (_port.Items.Contains(previous)) _port.SelectedItem = previous;
            else if (_port.Items.Count > 0) _port.SelectedIndex = 0;
        }

        private void Log(string line)
        {
            // Добавяме съобщение в долния панел и превъртаме до края.
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action)(() => Log(line))); return; }
            _output.AppendText(line + Environment.NewLine);
            _output.SelectionStart = _output.TextLength;
            _output.ScrollToCaret();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Затваряме серийния порт, ако в бъдеще е бил отворен.
            try { if (_serial.IsOpen) _serial.Close(); } catch { }
            base.OnFormClosing(e);
        }
    }
}