using System;
using System.Collections.Generic;
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
        private CodeEditor _editor;
        private TabControl _documents;
        private readonly Dictionary<TabPage, CodeEditor> _tabEditors = new Dictionary<TabPage, CodeEditor>();
        private readonly Dictionary<TabPage, string> _savedTexts = new Dictionary<TabPage, string>();
        private bool _switchingDocument = false;
        private Action _updateEditorStatus;
        private readonly TreeView _projectTree = new TreeView();
        private readonly TextBox _projectFilter = new TextBox();
        private readonly Label _projectSummary = new Label();
        private readonly ListView _projectProperties = new ListView();
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
            KeyPreview = true;
            KeyDown += MainFormKeyDown;
            // Стартираме максимизиран, за да има достатъчно място за редактора и кода.
            WindowState = FormWindowState.Maximized;
            BackColor = Color.FromArgb(238, 242, 250); // Светъл синьо-сив фон като Visual Studio 2019.
            ForeColor = Color.FromArgb(30, 30, 30);
            BuildInterface();
            FormClosing += ConfirmCloseWithUnsavedDocuments;
            NewSketch();
            RefreshPorts();
        }

        private void BuildInterface()
        {
            // Главното меню е подредено подобно на Visual Studio.
            var menu = new MenuStrip { BackColor = Color.FromArgb(238, 242, 250), ForeColor = Color.FromArgb(30, 30, 30), RenderMode = ToolStripRenderMode.Professional };
            // Единна цветова тема за падащите менюта и лентата с инструменти.
            menu.Renderer = new ToolStripProfessionalRenderer(new ArduinoColorTable());
            var fileMenu = new ToolStripMenuItem("Файл");
            fileMenu.DropDownItems.Add("Нов скеч", null, (s, e) => NewSketch());
            fileMenu.DropDownItems.Add("Отвори...", null, (s, e) => OpenFile());
            fileMenu.DropDownItems.Add("Запиши  Ctrl+S", null, (s, e) => SaveFile());
            fileMenu.DropDownItems.Add("Запиши всички", null, (s, e) => SaveAllDocuments());
            fileMenu.DropDownItems.Add("Запиши като...", null, (s, e) => SaveFileAs());
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("Затвори текущия таб  Ctrl+W", null, (s, e) => CloseDocument(_documents.SelectedTab, false));
            fileMenu.DropDownItems.Add("Затвори без запис", null, (s, e) => CloseDocument(_documents.SelectedTab, true));
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
            toolsMenu.DropDownItems.Add("Подготви компилатора (AVR)", null, (s, e) => InstallAvrCore());
            toolsMenu.DropDownItems.Add("Инсталирай библиотеката LiquidCrystal", null, (s, e) => InstallLiquidCrystal());
            toolsMenu.DropDownItems.Add("Справка за платките...", null, (s, e) => ShowBoardHelp());
            // Меню за редактиране, търсене и навигация в кода.
            var editMenu = new ToolStripMenuItem("Редактиране");
            editMenu.DropDownItems.Add("Намери следващо...   Ctrl+F", null, (s, e) => FindInEditor());
            editMenu.DropDownItems.Add("Търси във всички файлове на проекта...", null, (s, e) => FindInProjectFiles());
            editMenu.DropDownItems.Add("Замени всички...", null, (s, e) => ReplaceInEditor());
            editMenu.DropDownItems.Add("Отиди на ред...   Ctrl+G", null, (s, e) => GoToEditorLine());
            editMenu.DropDownItems.Add("Списък на функциите / символите", null, (s, e) => ShowCodeSymbols());
            editMenu.DropDownItems.Add("Постави / махни отметка  Ctrl+F2", null, (s, e) => _editor.ToggleBookmark());
            editMenu.DropDownItems.Add("Следваща отметка  F2", null, (s, e) => _editor.NextBookmark());
            editMenu.DropDownItems.Add("Изчисти отметките", null, (s, e) => _editor.ClearBookmarks());
            editMenu.DropDownItems.Add("Отстъпи избраните редове  Tab", null, (s, e) => _editor.IndentSelection(true));
            editMenu.DropDownItems.Add("Прибери избраните редове  Shift+Tab", null, (s, e) => _editor.IndentSelection(false));
            editMenu.DropDownItems.Add("Огради с блоков коментар /* ... */", null, (s, e) => _editor.ToggleBlockComment());
            editMenu.DropDownItems.Add(new ToolStripSeparator());
            editMenu.DropDownItems.Add("Дублирай ред / избран текст   Ctrl+D", null, (s, e) => _editor.DuplicateCurrentLineOrSelection());
            editMenu.DropDownItems.Add("Коментирай / разкоментирай   Ctrl+/", null, (s, e) => _editor.ToggleCommentSelection());
            editMenu.DropDownItems.Add("Премести ред нагоре", null, (s, e) => _editor.MoveCurrentLine(-1));
            editMenu.DropDownItems.Add("Премести ред надолу", null, (s, e) => _editor.MoveCurrentLine(1));
            editMenu.DropDownItems.Add("Почисти празните интервали в края на редовете", null, (s, e) => _editor.TrimTrailingWhitespace());
            editMenu.DropDownItems.Add("Сортирай избраните редове", null, (s, e) => _editor.SortSelectedLines());
            editMenu.DropDownItems.Add("Главни букви", null, (s, e) => _editor.ChangeSelectionCase(true));
            editMenu.DropDownItems.Add("Малки букви", null, (s, e) => _editor.ChangeSelectionCase(false));
            editMenu.DropDownItems.Add("Вмъкни дата и час", null, (s, e) => _editor.InsertDateTime());
            editMenu.DropDownItems.Add(new ToolStripSeparator());
            editMenu.DropDownItems.Add("Пренасяне на дългите редове", null, (s, e) => _editor.ToggleWordWrap());
            editMenu.DropDownItems.Add("Увеличи шрифта", null, (s, e) => _editor.ChangeEditorFontSize(1));
            editMenu.DropDownItems.Add("Намали шрифта", null, (s, e) => _editor.ChangeEditorFontSize(-1));
            editMenu.DropDownItems.Add("Нормален размер на шрифта", null, (s, e) => _editor.ResetEditorFontSize());
            // Меню „Проект“ събира операции върху папката и файловете в нея.
            var projectMenu = new ToolStripMenuItem("Проект");
            projectMenu.DropDownItems.Add("Отвори папка на проекта...", null, (s, e) => OpenProjectFolder());
            projectMenu.DropDownItems.Add("Обнови дървото на проекта", null, (s, e) => RefreshProjectTree());
            projectMenu.DropDownItems.Add("Търси във всички файлове на проекта...", null, (s, e) => FindInProjectFiles());
            projectMenu.DropDownItems.Add(new ToolStripSeparator());
            projectMenu.DropDownItems.Add("Нов .ino файл...", null, (s, e) => CreateProjectFile(".ino"));
            projectMenu.DropDownItems.Add("Нов C++ файл...", null, (s, e) => CreateProjectFile(".cpp"));
            projectMenu.DropDownItems.Add("Нов заглавен файл (.h)...", null, (s, e) => CreateProjectFile(".h"));
            projectMenu.DropDownItems.Add(new ToolStripSeparator());
            projectMenu.DropDownItems.Add("Отвори папката във File Explorer", null, (s, e) => OpenProjectFolderInExplorer());
            projectMenu.DropDownItems.Add("Компилирай проекта", null, (s, e) => CompileSketch());
            projectMenu.DropDownItems.Add("Качи към платката", null, (s, e) => UploadSketch());

            var windowMenu = new ToolStripMenuItem("Прозорец");
            windowMenu.DropDownItems.Add("Изчисти изхода", null, (s, e) => { _output.Clear(); Log("Изходът е изчистен."); });
            windowMenu.DropDownItems.Add("Запази изхода във файл...", null, (s, e) => SaveOutputToFile());
            windowMenu.DropDownItems.Add("Копирай целия изход", null, (s, e) => { if (_output.TextLength > 0) Clipboard.SetText(_output.Text); });

            var helpMenu = new ToolStripMenuItem("Помощ");
            helpMenu.DropDownItems.Add("Платки и FQBN...", null, (s, e) => ShowBoardHelp());
            helpMenu.DropDownItems.Add("За Arduino Studio 7", null, (s, e) =>
                MessageBox.Show(this, "Arduino Studio 7\\nСреда за редактиране, компилиране и качване на Arduino скечове.\\n\\nКомпилираните HEX файлове се записват в папката на скеча.", "За програмата", MessageBoxButtons.OK, MessageBoxIcon.Information));

            menu.Items.AddRange(new ToolStripItem[] { fileMenu, editMenu, projectMenu, buildMenu, toolsMenu, windowMenu, helpMenu });
            MainMenuStrip = menu;
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                // Клавишните комбинации за редактора работят и когато фокусът е в кода.
                if (e.Control && e.KeyCode == Keys.F) { e.SuppressKeyPress = true; FindInEditor(); }
                else if (e.Control && e.KeyCode == Keys.H) { e.SuppressKeyPress = true; ReplaceInEditor(); }
                else if (e.Control && e.KeyCode == Keys.G) { e.SuppressKeyPress = true; GoToEditorLine(); }
                else if (e.Control && e.KeyCode == Keys.S && e.Shift) { e.SuppressKeyPress = true; SaveFileAs(); }
                else if (e.Control && e.KeyCode == Keys.S) { e.SuppressKeyPress = true; SaveFile(); }
            };

            // Лента с действия, платка и сериен порт.
            var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, BackColor = Color.FromArgb(238, 242, 250), ForeColor = Color.FromArgb(30, 30, 30), RenderMode = ToolStripRenderMode.Professional };
            toolbar.Renderer = new ToolStripProfessionalRenderer(new ArduinoColorTable());
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

            // Много-документен редактор: всеки файл получава собствен таб и собствено състояние.
            _documents = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 4), HotTrack = true };
            _documents.SelectedIndexChanged += (s, e) => ActivateSelectedDocument();
            var tabMenu = new ContextMenuStrip();
            tabMenu.Items.Add("Запиши този файл", null, (s, e) => SaveDocument(GetContextTab(tabMenu)));
            tabMenu.Items.Add("Затвори този файл", null, (s, e) => CloseDocument(GetContextTab(tabMenu), false));
            tabMenu.Items.Add("Затвори без запис", null, (s, e) => CloseDocument(GetContextTab(tabMenu), true));
            tabMenu.Items.Add(new ToolStripSeparator());
            tabMenu.Items.Add("Затвори всички други файлове", null, (s, e) => CloseOtherDocuments(GetContextTab(tabMenu)));
            _documents.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    int index = GetTabIndexAt(_documents, e.Location);
                    if (index >= 0) { _documents.SelectedIndex = index; tabMenu.Show(_documents, e.Location); }
                }
            };
            _documents.ContextMenuStrip = tabMenu;

            // Десният панел е оформен като работно пространство: бързи действия, филтър, дърво и свойства.
            _projectTree.Dock = DockStyle.Fill;
            _projectTree.BackColor = Color.FromArgb(250, 251, 254);
            _projectTree.ForeColor = Color.FromArgb(38, 48, 68);
            _projectTree.BorderStyle = BorderStyle.None;
            _projectTree.HideSelection = false;
            _projectTree.FullRowSelect = true;
            _projectTree.ShowLines = true;
            _projectTree.ShowRootLines = false;
            _projectTree.LineColor = Color.FromArgb(205, 214, 229);
            _projectTree.ItemHeight = 24;
            _projectTree.DrawMode = TreeViewDrawMode.OwnerDrawText;
            _projectTree.DrawNode += (s, e) =>
            {
                e.DrawDefault = false;
                bool selected = (e.State & TreeNodeStates.Selected) != 0;
                using (var bg = new SolidBrush(selected ? Color.FromArgb(220, 232, 252) : _projectTree.BackColor))
                    e.Graphics.FillRectangle(bg, e.Bounds);
                string label = e.Node.Text;
                Color color = e.Node.Level == 0 ? Color.FromArgb(48, 91, 160) :
                    (e.Node.Tag is string && Directory.Exists((string)e.Node.Tag) ? Color.FromArgb(65, 103, 145) :
                    (Path.GetExtension(label).Equals(".ino", StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(35, 135, 105) :
                    (Path.GetExtension(label).Equals(".h", StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(170, 105, 40) : Color.FromArgb(55, 65, 85))));
                string prefix = e.Node.Level == 0 ? "▣  " :
                    (e.Node.Tag is string && Directory.Exists((string)e.Node.Tag) ? "▸  " :
                    (Path.GetExtension(label).Equals(".ino", StringComparison.OrdinalIgnoreCase) ? "●  " :
                    (Path.GetExtension(label).Equals(".h", StringComparison.OrdinalIgnoreCase) ? "◇  " : "›  ")));
                using (var brush = new SolidBrush(color))
                using (var font = new Font("Segoe UI", 9.0f, e.Node.Level == 0 ? FontStyle.Bold : FontStyle.Regular))
                    e.Graphics.DrawString(prefix + label, font, brush, e.Bounds.Left + 2, e.Bounds.Top + 3);
            };
            _projectTree.NodeMouseDoubleClick += (s,e) => { if (e.Node.Tag is string && File.Exists((string)e.Node.Tag)) LoadFile((string)e.Node.Tag); };
            // Контекстно меню на файловете и папките в Solution Explorer.
            var treeMenu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(248, 249, 252),
                ForeColor = Color.FromArgb(30, 30, 30),
                Renderer = new ToolStripProfessionalRenderer(new ArduinoColorTable())
            };
            treeMenu.Items.Add("Отвори файл", null, (s, e) =>
            {
                if (_projectTree.SelectedNode != null && _projectTree.SelectedNode.Tag is string)
                {
                    string path = (string)_projectTree.SelectedNode.Tag;
                    if (File.Exists(path)) LoadFile(path);
                }
            });
            treeMenu.Items.Add("Обнови изгледа", null, (s, e) => RefreshProjectTree());
            treeMenu.Items.Add("Копирай пътя", null, (s, e) =>
            {
                if (_projectTree.SelectedNode != null && _projectTree.SelectedNode.Tag is string)
                    Clipboard.SetText((string)_projectTree.SelectedNode.Tag);
            });
            treeMenu.Items.Add(new ToolStripSeparator());
            treeMenu.Items.Add("Нов .ino файл в проекта...", null, (s, e) => CreateProjectFile(".ino"));
            treeMenu.Items.Add("Нов C++ файл в проекта...", null, (s, e) => CreateProjectFile(".cpp"));
            treeMenu.Items.Add("Нов заглавен файл (.h)...", null, (s, e) => CreateProjectFile(".h"));
            treeMenu.Items.Add(new ToolStripSeparator());
            treeMenu.Items.Add("Компилирай проекта", null, (s, e) => CompileSketch());
            treeMenu.Items.Add("Качи към платката", null, (s, e) => UploadSketch());
            treeMenu.Items.Add("Отвори във File Explorer", null, (s, e) => OpenSelectedPathInExplorer());
            treeMenu.Items.Add("Изтрий избрания файл...", null, (s, e) => DeleteSelectedProjectFile());
            _projectTree.ContextMenuStrip = treeMenu;
            _projectTree.NodeMouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Right) _projectTree.SelectedNode = e.Node;
                if (e.Node != null) UpdateProjectProperties(e.Node.Tag as string);
            };
            _projectTree.AfterSelect += (s, e) => UpdateProjectProperties(e.Node == null ? null : e.Node.Tag as string);

            var projectPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(250, 251, 254), Padding = new Padding(0) };
            var projectHeader = SectionLabel("  PROJECT EXPLORER");
            projectHeader.BackColor = Color.FromArgb(53, 78, 125);
            projectHeader.ForeColor = Color.White;
            projectHeader.Font = new Font("Segoe UI", 9.0f, FontStyle.Bold);
            projectHeader.Dock = DockStyle.Top;
            projectHeader.Height = 32;

            var projectActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 36, Padding = new Padding(5, 4, 3, 2),
                WrapContents = false, AutoScroll = true, BackColor = Color.FromArgb(235, 240, 249)
            };
            projectActions.Controls.Add(SmallActionButton("+ .ino", (s,e) => CreateProjectFile(".ino")));
            projectActions.Controls.Add(SmallActionButton("+ C++", (s,e) => CreateProjectFile(".cpp")));
            projectActions.Controls.Add(SmallActionButton("+ .h", (s,e) => CreateProjectFile(".h")));
            projectActions.Controls.Add(SmallActionButton("↻", (s,e) => RefreshProjectTree()));

            var filterPanel = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(7, 5, 7, 4), BackColor = Color.FromArgb(245, 247, 252) };
            _projectFilter.Dock = DockStyle.Fill;
            _projectFilter.Font = new Font("Segoe UI", 9.0f);
            _projectFilter.BorderStyle = BorderStyle.FixedSingle;
            _projectFilter.ForeColor = Color.FromArgb(75, 85, 105);
            _projectFilter.TextChanged += (s,e) => RefreshProjectTree();
            filterPanel.Controls.Add(_projectFilter);

            _projectSummary.Dock = DockStyle.Bottom;
            _projectSummary.Height = 25;
            _projectSummary.TextAlign = ContentAlignment.MiddleLeft;
            _projectSummary.Padding = new Padding(8, 0, 0, 0);
            _projectSummary.BackColor = Color.FromArgb(235, 240, 249);
            _projectSummary.ForeColor = Color.FromArgb(85, 98, 122);
            _projectSummary.Font = new Font("Segoe UI", 8.0f);

            projectPanel.Controls.Add(_projectTree);
            projectPanel.Controls.Add(_projectSummary);
            projectPanel.Controls.Add(filterPanel);
            projectPanel.Controls.Add(projectActions);
            projectPanel.Controls.Add(projectHeader);

            _projectProperties.Dock = DockStyle.Fill;
            _projectProperties.View = View.Details;
            _projectProperties.FullRowSelect = true;
            _projectProperties.GridLines = false;
            _projectProperties.BorderStyle = BorderStyle.None;
            _projectProperties.BackColor = Color.FromArgb(248, 250, 253);
            _projectProperties.ForeColor = Color.FromArgb(45, 55, 75);
            _projectProperties.Font = new Font("Segoe UI", 8.5f);
            _projectProperties.Columns.Add("Свойство", 90);
            _projectProperties.Columns.Add("Стойност", 150);
            var propertiesPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            var propertiesHeader = SectionLabel("  FILE DETAILS");
            propertiesHeader.BackColor = Color.FromArgb(67, 108, 133);
            propertiesHeader.ForeColor = Color.White;
            propertiesHeader.Font = new Font("Segoe UI", 9.0f, FontStyle.Bold);
            propertiesHeader.Dock = DockStyle.Top;
            propertiesHeader.Height = 30;
            propertiesPanel.Controls.Add(_projectProperties);
            propertiesPanel.Controls.Add(propertiesHeader);

            var rightTools = new SplitContainer
            {
                Size = new Size(330, 700), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
                SplitterDistance = 430, Panel1MinSize = 180, Panel2MinSize = 100,
                FixedPanel = FixedPanel.Panel2, IsSplitterFixed = false,
                BackColor = Color.FromArgb(198, 207, 222), SplitterWidth = 6
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
            upper.Panel1.Controls.Add(_documents);
            upper.Panel2.Controls.Add(rightTools);

            // Долният прозорец за грешки и компилация е отделен и може да се преоразмерява.
            _output.Dock = DockStyle.Fill;
            _output.ReadOnly = true;
            _output.BackColor = Color.White;
            _output.ForeColor = Color.FromArgb(30, 30, 30);
            _output.Font = new Font("Consolas", 9.0f);
            _output.BorderStyle = BorderStyle.None;
            // Контекстно меню на изхода за копиране и запазване на съобщенията.
            var outputMenu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(248, 249, 252),
                ForeColor = Color.FromArgb(30, 30, 30),
                Renderer = new ToolStripProfessionalRenderer(new ArduinoColorTable())
            };
            outputMenu.Items.Add("Копирай избраното", null, (s, e) => { if (_output.SelectionLength > 0) Clipboard.SetText(_output.SelectedText); });
            outputMenu.Items.Add("Копирай целия изход", null, (s, e) => { if (_output.TextLength > 0) Clipboard.SetText(_output.Text); });
            outputMenu.Items.Add(new ToolStripSeparator());
            outputMenu.Items.Add("Изчисти изхода", null, (s, e) => _output.Clear());
            outputMenu.Items.Add("Запази изхода във файл...", null, (s, e) => SaveOutputToFile());
            _output.ContextMenuStrip = outputMenu;
            _output.MouseDoubleClick += (s, e) => NavigateFromBuildOutput();
            var lower = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(248, 249, 252) };
            var outputHeader = SectionLabel("OUTPUT / COMPILATION");
            outputHeader.BackColor = Color.FromArgb(83, 102, 153);
            outputHeader.ForeColor = Color.White;
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

            // Долна статусна лента с текущ ред, колона и размер на документа.
            var status = new StatusStrip { BackColor = Color.FromArgb(83, 102, 153), ForeColor = Color.White, SizingGrip = false };
            var statusName = new ToolStripStatusLabel("Arduino Studio 7");
            var statusLanguage = new ToolStripStatusLabel(" | Arduino / C++ |");
            var statusPosition = new ToolStripStatusLabel("Ред 1, колона 1");
            var statusLength = new ToolStripStatusLabel("Символи: 0");
            var statusReady = new ToolStripStatusLabel("Готово") { Spring = true, TextAlign = ContentAlignment.MiddleRight };
            status.Items.Add(statusName);
            status.Items.Add(statusLanguage);
            status.Items.Add(new ToolStripStatusLabel("|"));
            status.Items.Add(statusPosition);
            status.Items.Add(new ToolStripStatusLabel("|"));
            status.Items.Add(statusLength);
            status.Items.Add(statusReady);

            // Обновяваме информацията веднага при движение на курсора или промяна на текста.
            Action updateEditorStatus = () =>
            {
                if (_editor == null || _editor.IsDisposed) { statusPosition.Text = "Няма активен файл"; statusLength.Text = "Символи: 0"; return; }
                int line = _editor.GetLineFromCharIndex(_editor.SelectionStart);
                int lineStart = _editor.GetFirstCharIndexFromLine(line);
                statusPosition.Text = "Ред " + (line + 1) + ", колона " + (_editor.SelectionStart - Math.Max(0, lineStart) + 1);
                statusLength.Text = "Символи: " + _editor.TextLength;
            };
            _updateEditorStatus = updateEditorStatus;
            updateEditorStatus();

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

        // Цветова таблица за синьо-графитова тема, вдъхновена от Visual Studio.
        private sealed class ArduinoColorTable : ProfessionalColorTable
        {
            public override Color ToolStripGradientBegin { get { return Color.FromArgb(238, 242, 250); } }
            public override Color ToolStripGradientMiddle { get { return Color.FromArgb(238, 242, 250); } }
            public override Color ToolStripGradientEnd { get { return Color.FromArgb(238, 242, 250); } }
            public override Color MenuStripGradientBegin { get { return Color.FromArgb(238, 242, 250); } }
            public override Color MenuStripGradientEnd { get { return Color.FromArgb(238, 242, 250); } }
            public override Color MenuItemSelected { get { return Color.FromArgb(215, 228, 248); } }
            public override Color MenuItemBorder { get { return Color.FromArgb(120, 150, 205); } }
            public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(215, 228, 248); } }
            public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(215, 228, 248); } }
            public override Color MenuItemPressedGradientBegin { get { return Color.FromArgb(198, 216, 243); } }
            public override Color MenuItemPressedGradientMiddle { get { return Color.FromArgb(198, 216, 243); } }
            public override Color MenuItemPressedGradientEnd { get { return Color.FromArgb(198, 216, 243); } }
            public override Color ToolStripBorder { get { return Color.FromArgb(190, 200, 218); } }
            public override Color SeparatorDark { get { return Color.FromArgb(190, 200, 218); } }
            public override Color SeparatorLight { get { return Color.FromArgb(248, 249, 252); } }
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
                BackColor = Color.FromArgb(238, 242, 250), ForeColor = Color.FromArgb(30, 30, 30), Padding = new Padding(7,0,0,0) };
        }

        private void NewSketch()
        {
            // Всеки нов скеч отваря отделен таб, без да замества вече отворените файлове.
            string starter = "void setup()\r\n{\r\n    pinMode(LED_BUILTIN, OUTPUT);\r\n}\r\n\r\nvoid loop()\r\n{\r\n    digitalWrite(LED_BUILTIN, HIGH);\r\n    delay(500);\r\n    digitalWrite(LED_BUILTIN, LOW);\r\n    delay(500);\r\n}\r\n";
            AddDocumentTab(null, starter, "Нов скеч");
            _projectTree.Nodes.Clear();
            _projectTree.Nodes.Add(new TreeNode("Нов скеч (не е записан)"));
            Log("Създаден е нов таб. Запишете скеча с Файл → Запиши като.");
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
            // Ако файлът вече е отворен, активираме съществуващия таб вместо да го дублираме.
            try
            {
                string fullPath = Path.GetFullPath(path);
                foreach (TabPage page in _documents.TabPages)
                {
                    if (page.Tag is string && String.Equals(Path.GetFullPath((string)page.Tag), fullPath, StringComparison.OrdinalIgnoreCase))
                    { _documents.SelectedTab = page; return; }
                }
                string content = File.ReadAllText(fullPath);
                AddDocumentTab(fullPath, content, Path.GetFileName(fullPath));
                _projectFolder = Path.GetDirectoryName(fullPath);
                RefreshProjectTree();
                Log("Отворен файл в нов таб: " + fullPath);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Грешка при отваряне", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void SaveFile()
        {
            // Ctrl+S и менюто записват само активния документ.
            SaveDocument(_documents == null ? null : _documents.SelectedTab);
        }


        // Преди изход проверяваме всички табове и даваме възможност за запис.
        private void ConfirmCloseWithUnsavedDocuments(object sender, FormClosingEventArgs e)
        {
            foreach (TabPage page in _documents.TabPages.Cast<TabPage>().ToArray())
            {
                if (!_tabEditors.ContainsKey(page) || _savedTexts[page] == _tabEditors[page].Text) continue;
                _documents.SelectedTab = page;
                DialogResult answer = MessageBox.Show(this,
                    "Файлът \"" + page.Text.TrimEnd('*') + "\" има незаписани промени. Да бъде ли записан?",
                    "Незаписани промени", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (answer == DialogResult.Cancel) { e.Cancel = true; return; }
                if (answer == DialogResult.Yes)
                {
                    SaveDocument(page);
                    if (_savedTexts[page] != _tabEditors[page].Text) { e.Cancel = true; return; }
                }
            }
        }

        // Показва намерените функции, методи, класове и структури; двойното щракване отива на реда.
        private void ShowCodeSymbols()
        {
            if (_editor == null) return;
            var symbols = new List<Tuple<string, int>>();
            string[] lines = _editor.Lines;
            Regex pattern = new Regex(@"^\\s*(?:(?:static|inline|virtual|constexpr|unsigned|const)\\s+)*(?:[A-Za-z_][A-Za-z0-9_:<>*&]*\\s+)+([A-Za-z_][A-Za-z0-9_]*)\\s*\\([^;]*\\)\\s*(?:const\\s*)?(?:\\{|$)|^\\s*(class|struct|enum)\\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
            for (int i = 0; i < lines.Length; i++)
            {
                Match m = pattern.Match(lines[i]);
                if (!m.Success) continue;
                string name = !String.IsNullOrEmpty(m.Groups[2].Value) ? m.Groups[2].Value :
                    (!String.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[3].Value);
                if (!String.IsNullOrEmpty(name)) symbols.Add(Tuple.Create(name + "    — ред " + (i + 1), i));
            }
            using (Form dialog = new Form())
            using (ListBox list = new ListBox())
            using (Button go = new Button())
            {
                dialog.Text = "Функции и символи";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Size = new Size(420, 500);
                list.Dock = DockStyle.Fill;
                list.Font = new Font("Consolas", 10);
                foreach (var item in symbols) list.Items.Add(item.Item1);
                if (symbols.Count == 0) list.Items.Add("Не са намерени функции или типове.");
                go.Text = "Отиди към избрания символ";
                go.Dock = DockStyle.Bottom;
                go.Height = 34;
                Action navigate = () =>
                {
                    if (list.SelectedIndex < 0 || list.SelectedIndex >= symbols.Count) return;
                    int line = symbols[list.SelectedIndex].Item2;
                    int pos = _editor.GetFirstCharIndexFromLine(line);
                    if (pos >= 0) { _editor.Select(pos, 0); _editor.Focus(); _editor.ScrollToCaret(); }
                    dialog.Close();
                };
                list.DoubleClick += (s, e) => navigate();
                go.Click += (s, e) => navigate();
                dialog.Controls.Add(list);
                dialog.Controls.Add(go);
                dialog.ShowDialog(this);
            }
        }


        // Търси текст рекурсивно в .ino/.cpp/.h файловете на проекта и позволява преход към резултат.
        private void FindInProjectFiles()
        {
            string root = _projectFolder;
            if (String.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                using (var choose = new FolderBrowserDialog())
                {
                    choose.Description = "Изберете папката на Arduino проекта";
                    if (choose.ShowDialog(this) != DialogResult.OK) return;
                    root = choose.SelectedPath;
                }
            }
            using (var dialog = new Form())
            using (var query = new TextBox())
            using (var caseSensitive = new CheckBox())
            using (var search = new Button())
            using (var results = new ListBox())
            {
                dialog.Text = "Търсене в проекта";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Size = new Size(850, 520);
                dialog.MinimizeBox = false; dialog.MaximizeBox = true;
                query.SetBounds(12, 12, 560, 26);
                query.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                caseSensitive.Text = "С главни/малки букви"; caseSensitive.SetBounds(580, 14, 160, 24);
                caseSensitive.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                search.Text = "Търси"; search.SetBounds(745, 11, 75, 28);
                search.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                results.SetBounds(12, 48, 808, 420);
                results.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
                results.HorizontalScrollbar = true;
                var foundPaths = new List<string>();
                var foundLines = new List<int>();
                Action runSearch = () =>
                {
                    results.Items.Clear(); foundPaths.Clear(); foundLines.Clear();
                    if (String.IsNullOrEmpty(query.Text)) return;
                    try
                    {
                        string[] files = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                            .Where(p => new[] { ".ino", ".cpp", ".h", ".c", ".hpp" }.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(part => part == ".git" || part == "build" || part == "bin" || part == "obj"))
                            .ToArray();
                        StringComparison comparison = caseSensitive.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                        foreach (string file in files)
                        {
                            string[] lines;
                            try { lines = File.ReadAllLines(file); } catch { continue; }
                            for (int i = 0; i < lines.Length; i++)
                                if (lines[i].IndexOf(query.Text, comparison) >= 0)
                                {
                                    foundPaths.Add(file); foundLines.Add(i + 1);
                                    results.Items.Add(Path.GetFileName(file) + "  (ред " + (i + 1) + "): " + lines[i].Trim());
                                }
                        }
                        dialog.Text = "Търсене в проекта — намерени: " + results.Items.Count;
                    }
                    catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Грешка при търсене", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                };
                search.Click += (s, e) => runSearch();
                query.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; runSearch(); } };
                Action openResult = () =>
                {
                    int index = results.SelectedIndex;
                    if (index < 0 || index >= foundPaths.Count) return;
                    string path = foundPaths[index]; int line = foundLines[index];
                    LoadFile(path);
                    if (_editor != null) _editor.GoToLine(line);
                    dialog.Close();
                };
                results.DoubleClick += (s, e) => openResult();
                dialog.Controls.AddRange(new Control[] { query, caseSensitive, search, results });
                dialog.AcceptButton = search;
                dialog.ShowDialog(this);
            }
        }

        // При двойно щракване върху съобщение с номер на ред опитваме да отворим съответния ред.
        private void NavigateFromBuildOutput()
        {
            // Първо използваме избрания ред от изхода, иначе целия лог.
            string source = _output.SelectionLength > 0 ? _output.SelectedText : _output.Text;
            Match match = Regex.Match(source,
                @"(?<file>(?:[A-Za-z]:\\|/)?[^:\r\n]+?\.(?:ino|cpp|h|c|hpp)):(?<line>\d+)(?::(?<column>\d+))?",
                RegexOptions.IgnoreCase);
            string file = null;
            int line = 0, column = 1;
            if (match.Success)
            {
                file = match.Groups["file"].Value.Trim();
                Int32.TryParse(match.Groups["line"].Value, out line);
                if (match.Groups["column"].Success) Int32.TryParse(match.Groups["column"].Value, out column);
                if (!Path.IsPathRooted(file) && !String.IsNullOrEmpty(_projectFolder))
                    file = Path.GetFullPath(Path.Combine(_projectFolder, file));
                if (!File.Exists(file))
                {
                    string candidate = Path.Combine(_projectFolder ?? "", Path.GetFileName(file));
                    if (File.Exists(candidate)) file = candidate;
                }
                if (File.Exists(file)) LoadFile(file);
            }
            else
            {
                // Резервен вариант за кратки съобщения, които съдържат само номер на ред.
                Match number = Regex.Match(source, @"(?:\(|:)(\d+)(?:,\d+)?\)?");
                if (!number.Success || !Int32.TryParse(number.Groups[1].Value, out line)) return;
            }
            if (_editor == null || line < 1 || line > _editor.Lines.Length) return;
            int position = _editor.GetFirstCharIndexFromLine(line - 1);
            if (position >= 0)
            {
                position = Math.Min(_editor.TextLength, position + Math.Max(0, column - 1));
                _editor.Select(position, 0); _editor.Focus(); _editor.ScrollToCaret();
            }
        }

        private void SaveDocument(TabPage page)
        {
            if (page == null || !_tabEditors.ContainsKey(page)) return;
            CodeEditor editor = _tabEditors[page];
            string path = page.Tag as string;
            if (String.IsNullOrEmpty(path)) { SaveFileAsFor(page); return; }
            try
            {
                File.WriteAllText(path, editor.Text);
                _savedTexts[page] = editor.Text;
                UpdateTabTitle(page);
                if (page == _documents.SelectedTab) { _currentFile = path; _projectFolder = Path.GetDirectoryName(path); }
                RefreshProjectTree();
                Log("Записан: " + path);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Грешка при запис", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void SaveFileAs()
        {
            SaveFileAsFor(_documents == null ? null : _documents.SelectedTab);
        }

        private void SaveFileAsFor(TabPage page)
        {
            if (page == null || !_tabEditors.ContainsKey(page)) return;
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "Arduino скеч (*.ino)|*.ino|C++ файл (*.cpp)|*.cpp|Header файл (*.h)|*.h|Всички файлове|*.*";
                dialog.FileName = Path.GetFileNameWithoutExtension(page.Tag as string ?? "MySketch") + Path.GetExtension(page.Tag as string ?? ".ino");
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string path = dialog.FileName;
                    if (Path.GetExtension(path).Equals(".ino", StringComparison.OrdinalIgnoreCase))
                    {
                        string folder = Path.GetDirectoryName(path);
                        string stem = Path.GetFileNameWithoutExtension(path);
                        string targetFolder = Path.Combine(folder, stem);
                        Directory.CreateDirectory(targetFolder);
                        path = Path.Combine(targetFolder, Path.GetFileName(path));
                    }
                    if (_documents.TabPages.Cast<TabPage>().Any(p => p != page && p.Tag is string &&
                        String.Equals(Path.GetFullPath((string)p.Tag), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
                    {
                        MessageBox.Show(this, "Този файл вече е отворен в друг таб.", "Запиши като", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    page.Tag = path;
                    File.WriteAllText(path, _tabEditors[page].Text);
                    _savedTexts[page] = _tabEditors[page].Text;
                    _currentFile = path;
                    _projectFolder = Path.GetDirectoryName(path);
                    UpdateTabTitle(page);
                    RefreshProjectTree();
                    Log("Записан: " + path);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Грешка при запис", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        // Създаваме допълнителен файл в папката на проекта.
        // Глобални клавишни комбинации за бърза работа с документите.
        private void MainFormKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.Shift && e.KeyCode == Keys.S) { SaveAllDocuments(); e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.S) { SaveFile(); e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.W) { CloseDocument(_documents.SelectedTab, false); e.SuppressKeyPress = true; }
        }

        // Записва всички отворени документи; файловете без име ще поискат място за запис.
        private void SaveAllDocuments()
        {
            foreach (TabPage page in _documents.TabPages.Cast<TabPage>().ToArray())
                SaveDocument(page);
        }

        // Създава таб с отделен редактор и номерация на редовете в лявата лента.
        private void AddDocumentTab(string path, string content, string title)
        {
            var editor = new CodeEditor();
            editor.Dock = DockStyle.Fill;
            editor.Margin = Padding.Empty;
            editor.Text = content ?? String.Empty;
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = Padding.Empty };
            var gutter = new LineNumberGutter(editor) { Dock = DockStyle.Left, Width = 46 };
            host.Controls.Add(editor);
            host.Controls.Add(gutter);
            var page = new TabPage(title) { BackColor = Color.White, Padding = Padding.Empty, Tag = path };
            page.Controls.Add(host);
            _tabEditors[page] = editor;
            _savedTexts[page] = editor.Text;
            editor.FindRequested += (s, e) => FindInEditor();
            editor.BookmarksChanged += (s, e) => gutter.Invalidate();
            editor.TextChanged += (s, e) => { UpdateTabTitle(page); if (editor == _editor && _updateEditorStatus != null) _updateEditorStatus(); gutter.Invalidate(); };
            editor.SelectionChanged += (s, e) => { if (editor == _editor && _updateEditorStatus != null) _updateEditorStatus(); gutter.Invalidate(); };
            editor.Resize += (s, e) => gutter.Invalidate();
            editor.MouseWheel += (s, e) => gutter.Invalidate();
            editor.MouseUp += (s, e) => gutter.Invalidate();
            editor.KeyUp += (s, e) => gutter.Invalidate();
            _documents.TabPages.Add(page);
            _documents.SelectedTab = page;
            ActivateSelectedDocument();
        }

        // Активира избрания таб и синхронизира текущия файл, заглавието и статусната лента.
        private void ActivateSelectedDocument()
        {
            if (_switchingDocument || _documents == null || _documents.SelectedTab == null) return;
            TabPage page = _documents.SelectedTab;
            CodeEditor editor;
            if (!_tabEditors.TryGetValue(page, out editor)) return;
            _switchingDocument = true;
            try
            {
                _editor = editor;
                _currentFile = page.Tag as string ?? String.Empty;
                _projectFolder = String.IsNullOrEmpty(_currentFile) ? "" : Path.GetDirectoryName(_currentFile);
                Text = "Arduino Studio 7 — " + (String.IsNullOrEmpty(_currentFile) ? page.Text.TrimEnd('*') : Path.GetFileName(_currentFile));
                if (_updateEditorStatus != null) _updateEditorStatus();
            }
            finally { _switchingDocument = false; }
        }

        // Отбелязва незаписаните промени със звездичка в заглавието на таба.
        private void UpdateTabTitle(TabPage page)
        {
            if (page == null || !_tabEditors.ContainsKey(page)) return;
            string path = page.Tag as string;
            string name = String.IsNullOrEmpty(path) ? "Нов скеч" : Path.GetFileName(path);
            string saved;
            bool dirty = !_savedTexts.TryGetValue(page, out saved) || saved != _tabEditors[page].Text;
            page.Text = name + (dirty ? "*" : "");
        }

        // Записва само документа, избран от контекстното меню на таба.
        private TabPage GetContextTab(ContextMenuStrip menu)
        {
            return _documents == null ? null : _documents.SelectedTab;
        }

        private int GetTabIndexAt(TabControl tabs, Point point)
        {
            for (int i = 0; i < tabs.TabCount; i++)
                if (tabs.GetTabRect(i).Contains(point)) return i;
            return -1;
        }

        // Затваря избрания таб; стандартното затваряне предлага запис, а отделната команда пропуска записа.
        private void CloseDocument(TabPage page, bool discardWithoutSaving)
        {
            if (page == null || !_tabEditors.ContainsKey(page)) return;
            string saved;
            bool dirty = !_savedTexts.TryGetValue(page, out saved) || saved != _tabEditors[page].Text;
            if (dirty && !discardWithoutSaving)
            {
                DialogResult answer = MessageBox.Show(this, "Файлът има незаписани промени. Да бъде ли записан?\n\n" + page.Text.TrimEnd('*'),
                    "Затваряне на файл", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;
                if (answer == DialogResult.Yes) { SaveDocument(page); if (page.Text.EndsWith("*")) return; }
            }
            _switchingDocument = true;
            try
            {
                _documents.TabPages.Remove(page);
                CodeEditor editor = _tabEditors[page];
                _tabEditors.Remove(page);
                _savedTexts.Remove(page);
                editor.Dispose();
                page.Dispose();
            }
            finally { _switchingDocument = false; }
            if (_documents.TabPages.Count == 0) NewSketch();
            else ActivateSelectedDocument();
        }

        // Затваря всички останали табове, като пита за всеки файл с промени.
        private void CloseOtherDocuments(TabPage keep)
        {
            if (keep == null) return;
            foreach (TabPage page in _documents.TabPages.Cast<TabPage>().Where(p => p != keep).ToArray())
            {
                CloseDocument(page, false);
            }
        }

        // Отделна лента за номерата на редовете; използва реалната позиция на реда в RichTextBox.
        private sealed class LineNumberGutter : Panel
        {
            private readonly CodeEditor _code;
            public LineNumberGutter(CodeEditor editor) { _code = editor; BackColor = Color.FromArgb(245, 247, 251); BorderStyle = BorderStyle.None; }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.Clear(BackColor);
                using (var pen = new Pen(Color.FromArgb(218, 223, 232))) e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
                using (var brush = new SolidBrush(Color.FromArgb(105, 115, 135)))
                using (var font = new Font("Consolas", 9.0f))
                {
                    for (int line = 0; line < _code.Lines.Length; line++)
                    {
                        int first = _code.GetFirstCharIndexFromLine(line);
                        if (first < 0) continue;
                        Point pos = _code.GetPositionFromCharIndex(first);
                        if (pos.Y < -font.Height || pos.Y > Height) continue;
                        if (_code.IsBookmarked(line))
                        {
                            using (var marker = new SolidBrush(Color.FromArgb(220, 70, 55)))
                                e.Graphics.FillEllipse(marker, 3, pos.Y + 4, 8, 8);
                        }
                        string number = (line + 1).ToString();
                        SizeF size = e.Graphics.MeasureString(number, font);
                        e.Graphics.DrawString(number, font, brush, Width - size.Width - 7, pos.Y + 1);
                    }
                }
            }
        }

        private void CreateProjectFile(string extension)
        {
            if (String.IsNullOrEmpty(_projectFolder) || !Directory.Exists(_projectFolder))
            {
                MessageBox.Show(this, "Първо запишете скеча или отворете папка на проект.", "Няма отворен проект",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string suggested = extension == ".ino" ? "ДопълнителенСкеч" : (extension == ".h" ? "MyHeader" : "MySource");
            string name = AskText("Нов файл", "Име на файла (без разширение):", suggested);
            if (String.IsNullOrWhiteSpace(name)) return;
            name = Path.GetFileNameWithoutExtension(name.Trim());
            if (String.IsNullOrWhiteSpace(name)) return;
            string path = Path.Combine(_projectFolder, name + extension);
            if (File.Exists(path))
            {
                MessageBox.Show(this, "Файлът вече съществува:\\n" + path, "Нов файл", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                string starter = extension == ".ino" ? "// Допълнителен Arduino скеч.\\n\\n" :
                    (extension == ".h" ? "#pragma once\\n\\n// Декларации за проекта.\\n" : "// C++ изходен файл за Arduino проекта.\\n\\n");
                File.WriteAllText(path, starter);
                Log("Създаден файл: " + path);
                RefreshProjectTree();
                if (extension != ".ino") LoadFile(path);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Грешка при създаване на файл", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        // Отваряме папката на проекта във File Explorer.
        private void OpenProjectFolderInExplorer()
        {
            if (!String.IsNullOrEmpty(_projectFolder) && Directory.Exists(_projectFolder))
                System.Diagnostics.Process.Start("explorer.exe", Quote(_projectFolder));
            else MessageBox.Show(this, "Няма отворена папка на проект.", "Проект", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // Отваряме местоположението на избрания файл или папка.
        private void OpenSelectedPathInExplorer()
        {
            if (_projectTree.SelectedNode == null) return;
            string path = _projectTree.SelectedNode.Tag as string;
            if (String.IsNullOrEmpty(path)) return;
            string target = File.Exists(path) ? Path.GetDirectoryName(path) : path;
            if (Directory.Exists(target)) System.Diagnostics.Process.Start("explorer.exe", Quote(target));
        }

        // Изтриването е само за файл и винаги изисква потвърждение.
        private void DeleteSelectedProjectFile()
        {
            if (_projectTree.SelectedNode == null) return;
            string path = _projectTree.SelectedNode.Tag as string;
            if (String.IsNullOrEmpty(path) || !File.Exists(path))
            {
                MessageBox.Show(this, "Изберете файл, а не папка.", "Изтриване", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (String.Equals(path, _currentFile, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Не може да изтриете отворения файл. Отворете друг файл първо.",
                    "Изтриване", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(this, "Наистина ли да изтриете файла?\\n\\n" + path, "Потвърждение",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { File.Delete(path); RefreshProjectTree(); Log("Изтрит файл: " + path); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Грешка при изтриване", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        // Записваме логовете от компилацията във файл за диагностика.
        private void SaveOutputToFile()
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = "Запази изхода";
                dialog.Filter = "Текстов файл (*.txt)|*.txt|Всички файлове (*.*)|*.*";
                dialog.FileName = "ArduinoStudio7-output.txt";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dialog.FileName, _output.Text); }
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

        // Обновява дървото с реални папки, филтър и брояч на файловете.
        private void RefreshProjectTree()
        {
            _projectTree.BeginUpdate();
            try
            {
                _projectTree.Nodes.Clear();
                if (String.IsNullOrEmpty(_projectFolder) || !Directory.Exists(_projectFolder))
                {
                    if (!String.IsNullOrEmpty(_currentFile))
                        _projectTree.Nodes.Add(new TreeNode(Path.GetFileName(_currentFile)) { Tag = _currentFile });
                    _projectSummary.Text = "  Няма отворена папка на проект";
                    return;
                }

                string rootPath = _projectFolder.TrimEnd(Path.DirectorySeparatorChar);
                string filter = (_projectFilter.Text ?? "").Trim();
                var allowed = Directory.GetFiles(rootPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => Regex.IsMatch(Path.GetExtension(f), @"^\.(ino|h|hpp|c|cpp)$", RegexOptions.IgnoreCase))
                    .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part =>
                        String.Equals(part, "build", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(part, "bin", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(part, "obj", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(part, ".git", StringComparison.OrdinalIgnoreCase)))
                    .Where(f => filter.Length == 0 || Path.GetFileName(f).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        Path.GetExtension(f).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        f.Substring(rootPath.Length).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();

                var root = new TreeNode(Path.GetFileName(rootPath)) { Tag = rootPath };
                var folderNodes = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
                folderNodes[rootPath] = root;
                foreach (string file in allowed)
                {
                    string relative = file.Substring(rootPath.Length).TrimStart(Path.DirectorySeparatorChar);
                    string[] parts = relative.Split(Path.DirectorySeparatorChar);
                    string current = rootPath;
                    TreeNode parent = root;
                    for (int i = 0; i < parts.Length - 1; i++)
                    {
                        current = Path.Combine(current, parts[i]);
                        TreeNode folder;
                        if (!folderNodes.TryGetValue(current, out folder))
                        {
                            folder = new TreeNode(parts[i]) { Tag = current };
                            folderNodes[current] = folder;
                            parent.Nodes.Add(folder);
                        }
                        parent = folder;
                    }
                    parent.Nodes.Add(new TreeNode(parts[parts.Length - 1]) { Tag = file });
                }
                root.Expand();
                _projectTree.Nodes.Add(root);
                _projectSummary.Text = "  " + allowed.Length + " файла  •  " + folderNodes.Count + " папки";
            }
            catch (Exception ex)
            {
                _projectSummary.Text = "  Грешка при четене на проекта";
                Log("Неуспешно обновяване на дървото: " + ex.Message);
            }
            finally { _projectTree.EndUpdate(); }
        }

        // Малки цветни бутони за често използваните действия в десния панел.
        private Button SmallActionButton(string caption, EventHandler action)
        {
            var button = new Button
            {
                Text = caption, AutoSize = true, Height = 26, FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(255, 255, 255), ForeColor = Color.FromArgb(55, 78, 118),
                Font = new Font("Segoe UI", 8.0f, FontStyle.Bold),
                Margin = new Padding(2, 0, 3, 0), Padding = new Padding(5, 1, 5, 1),
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(205, 216, 235);
            button.Click += action;
            return button;
        }

        // Показва подробности за избрания файл или директория в долния десен панел.
        private void UpdateProjectProperties(string path)
        {
            if (_projectProperties == null || _projectProperties.IsDisposed) return;
            _projectProperties.BeginUpdate();
            try
            {
                _projectProperties.Items.Clear();
                if (String.IsNullOrEmpty(path))
                {
                    AddProperty("Проект", String.IsNullOrEmpty(_projectFolder) ? "Няма" : Path.GetFileName(_projectFolder));
                    AddProperty("Платка", _board.Text);
                    AddProperty("COM порт", _port.Text);
                    return;
                }
                AddProperty("Име", Path.GetFileName(path));
                AddProperty("Тип", Directory.Exists(path) ? "Папка" : Path.GetExtension(path).ToUpperInvariant());
                AddProperty("Път", path);
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    AddProperty("Размер", info.Length.ToString("N0") + " байта");
                    AddProperty("Променен", info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
                    AddProperty("Редове", File.ReadAllLines(path).Length.ToString());
                }
                else if (Directory.Exists(path))
                    AddProperty("Файлове", Directory.GetFiles(path, "*.*", SearchOption.TopDirectoryOnly).Length.ToString());
                AddProperty("Платка", _board.Text);
            }
            catch (Exception ex) { AddProperty("Грешка", ex.Message); }
            finally { _projectProperties.EndUpdate(); }
        }

        // Добавя един ред към списъка с подробности.
        private void AddProperty(string name, string value)
        {
            var item = new ListViewItem(name);
            item.SubItems.Add(value ?? "");
            _projectProperties.Items.Add(item);
        }

        // Откриваме копие на LiquidCrystal.cpp в самата папка на скеча.
        // Ако то е там заедно с инсталираната библиотека, линкерът получава двойни дефиниции.
        private bool CheckDuplicateLibrarySources(string sketchFolder)
        {
            string duplicate = Directory.GetFiles(sketchFolder, "*.cpp", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => String.Equals(Path.GetFileName(path), "LiquidCrystal.cpp", StringComparison.OrdinalIgnoreCase));

            if (duplicate == null) return true;

            Log("Грешка: дублиран източник на LiquidCrystal: " + duplicate);
            Log("Инсталираната библиотека също съдържа LiquidCrystal.cpp, затова компилацията би дала multiple definition.");
            Log("Ако файлът е копие на библиотеката, преместете LiquidCrystal.cpp (и евентуално LiquidCrystal.h) извън папката на скеча.");
            MessageBox.Show(this,
                "В папката на скеча има LiquidCrystal.cpp. Arduino CLI компилира и този файл, и инсталираната библиотека, което причинява multiple definition.\n\nАко файлът е копие на библиотеката, преместете LiquidCrystal.cpp и евентуалния LiquidCrystal.h извън папката на скеча. После опитайте отново.",
                "Дублирана библиотека", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        private void CompileSketch()
        {
            // Записваме промените и подаваме папката на скеча към Arduino CLI.
            SaveFile();
            if (String.IsNullOrEmpty(_currentFile) || !File.Exists(_currentFile)) return;
            string sketchFolder = Path.GetDirectoryName(_currentFile);
            if (!CheckDuplicateLibrarySources(sketchFolder)) return;
            if (!Directory.GetFiles(sketchFolder, "*.ino", SearchOption.TopDirectoryOnly).Any())
            {
                Log("Грешка: Arduino CLI очаква .ino файл в папката на скеча.");
                return;
            }
            // Записваме компилираните файлове в същата папка, където е .ino файлът.
            // Така HEX файлът се намира непосредствено до проекта и не се губи в отделна build папка.
            RunCli("compile --fqbn " + Quote(_board.Text.Trim()) + " --output-dir " + Quote(sketchFolder) + " " + Quote(sketchFolder), sketchFolder);
            Log("Изходните файлове и HEX се записват в папката на скеча: " + sketchFolder);
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

        // Подготвяме Arduino CLI при първа употреба: индексите, вградените инструменти и AVR ядрото.
        private void InstallAvrCore()
        {
            var answer = MessageBox.Show(this,
                "Ще се изтеглят необходимите инструменти и AVR пакет за Uno/Mega/Nano. Нужен е интернет. Да продължа ли?",
                "Подготовка на компилатора", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            var worker = new System.ComponentModel.BackgroundWorker();
            worker.DoWork += (s, e) =>
            {
                try
                {
                    string folder = AppDomain.CurrentDomain.BaseDirectory;
                    Action<string> report = line => BeginInvoke((Action)(() => Log(line)));

                    // Първото стартиране може да съобщи, че липсват вградените инструменти.
                    // Обновяването на индекса ги изтегля/подготвя за следващите команди.
                    report("Стъпка 1/2: обновяване на индексите и вградените инструменти...");
                    string result = _toolchain.Run("core update-index", folder, report);
                    if (result != "0")
                    {
                        e.Result = "Обновяването на индексите не успя. Проверете интернет връзката и съобщенията по-горе.";
                        return;
                    }

                    report("Стъпка 2/2: инсталиране на arduino:avr...");
                    result = _toolchain.Run("core install arduino:avr", folder, report);
                    e.Result = result == "0"
                        ? "Готово. AVR компилаторът е инсталиран; пробвайте „Скица → Провери / Компилирай“."
                        : "Инсталирането на arduino:avr не успя. Проверете грешките в панела Output.";
                }
                catch (Exception ex) { e.Result = "Грешка при подготовката: " + ex.Message; }
            };
            worker.RunWorkerCompleted += (s, e) => Log(Convert.ToString(e.Result));
            worker.RunWorkerAsync();
        }

        // Инсталираме LiquidCrystal, когато скечът използва LCD дисплей и библиотеката липсва.
        private void InstallLiquidCrystal()
        {
            var answer = MessageBox.Show(this,
                "Ще се изтегли библиотеката LiquidCrystal от Arduino Library Manager. Нужен е интернет. Да продължа ли?",
                "Инсталиране на LiquidCrystal", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            var worker = new System.ComponentModel.BackgroundWorker();
            worker.DoWork += (s, e) =>
            {
                try
                {
                    string folder = AppDomain.CurrentDomain.BaseDirectory;
                    Action<string> report = line => BeginInvoke((Action)(() => Log(line)));
                    report("Обновяване на индекса на библиотеките...");
                    string result = _toolchain.Run("lib update-index", folder, report);
                    if (result != "0")
                    {
                        e.Result = "Индексът на библиотеките не се обнови. Проверете интернет връзката и изхода.";
                        return;
                    }

                    report("Инсталиране на библиотеката LiquidCrystal...");
                    result = _toolchain.Run("lib install LiquidCrystal", folder, report);
                    e.Result = result == "0"
                        ? "LiquidCrystal е инсталирана. Опитайте отново „Скица → Провери / Компилирай“."
                        : "Неуспешна инсталация на LiquidCrystal. Проверете грешките в панела Output.";
                }
                catch (Exception ex) { e.Result = "Грешка при инсталиране на LiquidCrystal: " + ex.Message; }
            };
            worker.RunWorkerCompleted += (s, e) => Log(Convert.ToString(e.Result));
            worker.RunWorkerAsync();
        }

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