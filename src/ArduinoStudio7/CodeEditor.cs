using System;
using System.Collections.Generic;
using System.Linq; // LINQ: използва се за филтриране и проверка на редовете при коментиране.
using System.Drawing;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ArduinoStudio7
{
    // Редактор в светлата цветова схема на Visual Studio 2019, с оцветяване и подсказки.
    public sealed class CodeEditor : RichTextBox
    {
        // Win32 съобщения за запазване на видимия ред при оцветяване.
        private const int WM_SETREDRAW = 0x000B;
        private const int EM_GETSCROLLPOS = 0x04DD;
        private const int EM_SETSCROLLPOS = 0x04DE;

        // Точка за запазване на точната хоризонтална и вертикална позиция на скрола.
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativePoint { public int X; public int Y; }

        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly ToolTip _hint = new ToolTip();
        // Подсказките се обработват само след като мишката остане неподвижна 2 секунди.
        private readonly Timer _hintTimer = new Timer();
        private Point _lastMousePoint;
        private string _lastHintWord = null;
        private string _lastHintText = null;
        private bool _hintVisible = false;
        private readonly Timer _highlightTimer = new Timer();
        private readonly ContextMenuStrip _completionMenu = new ContextMenuStrip();
        private readonly HashSet<int> _bookmarks = new HashSet<int>(); // Отметки редове за навигация в текущия файл.
        private readonly HashSet<string> _keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "void","bool","boolean","byte","char","unsigned","signed","short","int","long","float","double",
            "const","static","volatile","return","if","else","for","while","do","switch","case","break",
            "continue","default","struct","class","enum","typedef","sizeof","true","false","nullptr",
            "HIGH","LOW","INPUT","OUTPUT","INPUT_PULLUP","LED_BUILTIN","pinMode","digitalWrite",
            "digitalRead","analogRead","analogWrite","delay","delayMicroseconds","millis","micros",
            "attachInterrupt","detachInterrupt","Serial","Serial1","Serial2","Serial3","begin","println","print",
            "setup","loop","PROGMEM","String","word","size_t"
        };

        public CodeEditor()
        {
            // Настройваме моноширинен шрифт и цветове, наподобяващи Visual Studio.
            Font = new Font("Consolas", 10.0f);
            // ToolTip се показва ръчно след 2 секунди неподвижна мишка.
            _hint.InitialDelay = 2000;
            _hint.ReshowDelay = 2000;
            _hint.AutoPopDelay = 10000;
            _hint.ShowAlways = true;
            _hintTimer.Interval = 2000;
            _hintTimer.Tick += delegate { _hintTimer.Stop(); ShowHintForCurrentMousePosition(); };
            BackColor = Color.White;
            ForeColor = Color.FromArgb(30, 30, 30);
            BorderStyle = BorderStyle.None;
            AcceptsTab = true;
            WordWrap = false;
            DetectUrls = false;
            HideSelection = false;
            // Отлагаме оцветяването, за да остане въвеждането плавно.
            _highlightTimer.Interval = 700; // Не оцветяваме при всяко кратко спиране на курсора — пазим плавното писане и скролиране.
            _highlightTimer.Tick += delegate { _highlightTimer.Stop(); HighlightSyntax(); };
            TextChanged += delegate { _highlightTimer.Stop(); _highlightTimer.Start(); };
            MouseMove += EditorMouseMove;
            MouseLeave += delegate { _hintTimer.Stop(); HideHint(); };
            MouseDown += EditorMouseDown;
            KeyDown += EditorKeyDown;
            // Контекстно меню с често използвани команди за редактиране.
            var editorMenu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(248, 249, 252), // Светъл фон на контекстното меню.
                ForeColor = Color.FromArgb(30, 30, 30), // Тъмен текст за добра четимост.
                Renderer = new ToolStripProfessionalRenderer(new EditorColorTable())
            };
            // Основни команди за редактиране — с проверка дали има действие за Undo/Redo.
            editorMenu.Items.Add("Отмени  Ctrl+Z", null, (s, e) => { if (CanUndo) Undo(); });
            editorMenu.Items.Add("Повтори  Ctrl+Y", null, (s, e) => { if (CanRedo) Redo(); });
            editorMenu.Items.Add(new ToolStripSeparator());
            editorMenu.Items.Add("Изрежи", null, (s, e) => Cut());
            editorMenu.Items.Add("Копирай", null, (s, e) => Copy());
            editorMenu.Items.Add("Постави", null, (s, e) => Paste());
            editorMenu.Items.Add("Избери всичко", null, (s, e) => SelectAll());
            editorMenu.Items.Add("Избери текущия ред", null, (s, e) => SelectCurrentLine());
            editorMenu.Items.Add(new ToolStripSeparator());
            editorMenu.Items.Add("Дублирай ред / избор  Ctrl+D", null, (s, e) => DuplicateCurrentLineOrSelection());
            editorMenu.Items.Add("Коментирай / разкоментирай  Ctrl+/", null, (s, e) => ToggleCommentSelection());
            editorMenu.Items.Add("Намери в кода  Ctrl+F", null, (s, e) => RequestFind());
            editorMenu.Items.Add("Към ред…  Ctrl+G", null, (s, e) => PromptGoToLine());
            editorMenu.Items.Add("Към дефиницията  Ctrl+щракване", null, (s, e) => NavigateFromCaret());
            editorMenu.Items.Add("Постави / махни отметка  Ctrl+F2", null, (s, e) => ToggleBookmark());
            editorMenu.Items.Add("Следваща отметка  F2", null, (s, e) => NextBookmark());
            editorMenu.Items.Add("Изчисти отметките", null, (s, e) => ClearBookmarks());
            editorMenu.Items.Add("Отстъпи избраните редове  Tab", null, (s, e) => IndentSelection(true));
            editorMenu.Items.Add("Прибери избраните редове  Shift+Tab", null, (s, e) => IndentSelection(false));
            editorMenu.Items.Add("Огради с /* ... */", null, (s, e) => ToggleBlockComment());
            // Подменю с често използвани Arduino/C++ конструкции.
            var templates = new ToolStripMenuItem("Вмъкни шаблон");
            templates.DropDownItems.Add("setup() и loop()", null, (s, e) => InsertTemplate("void setup()\r\n{\r\n    // Инициализация\r\n}\r\n\r\nvoid loop()\r\n{\r\n    // Основен цикъл\r\n}\r\n"));
            templates.DropDownItems.Add("if / else", null, (s, e) => InsertTemplate("if (условие)\r\n{\r\n    \r\n}\r\nelse\r\n{\r\n    \r\n}\r\n"));
            templates.DropDownItems.Add("for цикъл", null, (s, e) => InsertTemplate("for (int i = 0; i < 10; i++)\r\n{\r\n    \r\n}\r\n"));
            templates.DropDownItems.Add("Serial.begin(9600)", null, (s, e) => InsertTemplate("Serial.begin(9600);"));
            templates.DropDownItems.Add("pinMode()", null, (s, e) => InsertTemplate("pinMode(LED_BUILTIN, OUTPUT);"));
            templates.DropDownItems.Add("digitalWrite()", null, (s, e) => InsertTemplate("digitalWrite(LED_BUILTIN, HIGH);"));
            editorMenu.Items.Add(templates);
            editorMenu.Items.Add(new ToolStripSeparator());
            // Допълнителни инструменти за по-бърза редакция на големи скечове.
            editorMenu.Items.Add("Премести ред нагоре", null, (s, e) => MoveCurrentLine(-1));
            editorMenu.Items.Add("Премести ред надолу", null, (s, e) => MoveCurrentLine(1));
            editorMenu.Items.Add("Почисти празните интервали в края на редовете", null, (s, e) => TrimTrailingWhitespace());
            editorMenu.Items.Add("Сортирай избраните редове", null, (s, e) => SortSelectedLines());
            editorMenu.Items.Add("Главни букви", null, (s, e) => ChangeSelectionCase(true));
            editorMenu.Items.Add("Малки букви", null, (s, e) => ChangeSelectionCase(false));
            editorMenu.Items.Add("Вмъкни дата и час", null, (s, e) => InsertDateTime());
            editorMenu.Items.Add(new ToolStripSeparator());
            editorMenu.Items.Add("Пренасяне на дългите редове", null, (s, e) => ToggleWordWrap());
            editorMenu.Items.Add("Увеличи шрифта", null, (s, e) => ChangeEditorFontSize(1));
            editorMenu.Items.Add("Намали шрифта", null, (s, e) => ChangeEditorFontSize(-1));
            editorMenu.Items.Add("Нормален размер на шрифта", null, (s, e) => ResetEditorFontSize());
            ContextMenuStrip = editorMenu;
            Disposed += delegate { _hint.Dispose(); _hintTimer.Dispose(); _highlightTimer.Dispose(); _completionMenu.Dispose(); editorMenu.Dispose(); };
        }

        // Цветове на контекстното меню — светлосиньо маркиране като във Visual Studio 2019.
        private sealed class EditorColorTable : ProfessionalColorTable
        {
            public override Color ToolStripGradientBegin { get { return Color.FromArgb(248, 249, 252); } }
            public override Color ToolStripGradientMiddle { get { return Color.FromArgb(248, 249, 252); } }
            public override Color ToolStripGradientEnd { get { return Color.FromArgb(248, 249, 252); } }
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

        // Позволява на главния прозорец да поиска оцветяване веднага след зареждане на файл.
        public void RefreshSyntaxHighlighting()
        {
            _highlightTimer.Stop(); // Не оставяме старо отложено оцветяване да се изпълни повторно.
            if (TextLength == 0) return; // Празният редактор няма какво да оцветява.
            if (IsHandleCreated) HighlightSyntax(); // Оцветяваме веднага, когато контролът вече е създаден.
            else _highlightTimer.Start(); // Ако още няма прозоречна дръжка, оставяме таймера да го направи по-късно.
        }

        private void HighlightSyntax()
        {
            // Запазваме каретката и точната позиция на скрола преди оцветяване.
            if (TextLength == 0 || !IsHandleCreated) return;
            int caret = SelectionStart;
            int selectionLength = SelectionLength;
            NativePoint scrollPosition = new NativePoint();
            SendMessage(Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref scrollPosition);
            SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                // Базовият цвят е тъмносив, подобно на светлата тема на Visual Studio.
                SelectAll();
                SelectionColor = Color.FromArgb(30, 30, 30);

                // Препроцесорни директиви: #include, #define, #if и други.
                string source = Text; // Единствено копие на текста за цялото оцветяване — избягваме повтарящи се копирания.
                ApplyColor(source, @"(?m)^\s*#\s*\w+.*$", Color.FromArgb(175, 90, 190));
                // Текстови константи и числа.
                ApplyColor(source, @"(?:""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*')", Color.FromArgb(163, 21, 21));
                ApplyColor(source, @"\b\d+(?:\.\d+)?\b", Color.FromArgb(9, 134, 88));

                // Един проход през идентификаторите; проверяваме следващия знак директно вместо да търсим наново остатъка от файла.
                foreach (Match match in Regex.Matches(source, @"\b[A-Za-z_][A-Za-z0-9_]*\b"))
                {
                    string word = match.Value;
                    Color? tokenColor = null;
                    if (_keywords.Contains(word))
                        tokenColor = Color.FromArgb(0, 0, 220); // Ключови думи и Arduino константи.
                    else if (Regex.IsMatch(word, @"^(?:uint\d+_t|int\d+_t|size_t|String|HardwareSerial)$"))
                        tokenColor = Color.FromArgb(43, 145, 175); // Често използвани типове.
                    else
                    {
                        int next = match.Index + match.Length;
                        while (next < source.Length && Char.IsWhiteSpace(source[next])) next++;
                        if (next < source.Length && source[next] == '(')
                            tokenColor = Color.FromArgb(121, 94, 38); // Имена на функции.
                    }

                    if (tokenColor.HasValue)
                    {
                        Select(match.Index, match.Length);
                        SelectionColor = tokenColor.Value;
                    }
                }
                // Коментарите се оцветяват последни, за да запазят зеления цвят навсякъде.
                ApplyColor(source, @"//[^\r\n]*|/\*[\s\S]*?\*/", Color.FromArgb(0, 128, 0));

                // Връщаме избора и позицията на курсора.
                Select(Math.Min(caret, TextLength), Math.Min(selectionLength, Math.Max(0, TextLength - caret)));
            }
            finally
            {
                // Възстановяваме скрола чрез Win32 позиция, без да го местим към каретката.
                SendMessage(Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref scrollPosition);
                SendMessage(Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                Invalidate();
            }
        }

        private void ApplyColor(string source, string pattern, Color color)
        {
            foreach (Match match in Regex.Matches(source, pattern))
            {
                Select(match.Index, match.Length);
                SelectionColor = color;
            }
        }


        private void EditorKeyDown(object sender, KeyEventArgs e)
        {
            // Enter наследява отместването на предходния ред.
            if (e.KeyCode == Keys.Enter && !e.Control && !e.Alt)
            {
                int lineStart = Text.LastIndexOf('\n', Math.Max(0, SelectionStart - 1));
                lineStart = lineStart < 0 ? 0 : lineStart + 1;
                string previousLine = Text.Substring(lineStart, SelectionStart - lineStart);
                string indent = Regex.Match(previousLine, @"^\s*").Value;
                if (previousLine.TrimEnd().EndsWith("{")) indent += "    ";
                e.SuppressKeyPress = true;
                SelectedText = Environment.NewLine + indent;
                return;
            }

            // Ctrl+A маркира целия код.
            if (e.Control && e.KeyCode == Keys.A) { SelectAll(); e.SuppressKeyPress = true; return; }
            // Ctrl+F отваря търсенето, а Ctrl+G — преход към ред.
            if (e.Control && e.KeyCode == Keys.F) { RequestFind(); e.SuppressKeyPress = true; return; }
            if (e.Control && e.KeyCode == Keys.G) { PromptGoToLine(); e.SuppressKeyPress = true; return; }
            // Ctrl+Z / Ctrl+Y използват стандартната undo/redo поддръжка на RichTextBox.
            if (e.Control && e.KeyCode == Keys.Z) { Undo(); e.SuppressKeyPress = true; return; }
            if (e.Control && e.KeyCode == Keys.Y) { if (CanRedo) Redo(); e.SuppressKeyPress = true; return; }

            // Ctrl+D дублира текущия ред или избрания блок код.
            if (e.Control && e.KeyCode == Keys.D)
            {
                e.SuppressKeyPress = true;
                DuplicateCurrentLineOrSelection();
                return;
            }

            // Ctrl+F2 поставя/премахва отметка, F2 преминава към следващата.
            if (e.Control && e.KeyCode == Keys.F2) { e.SuppressKeyPress = true; ToggleBookmark(); return; }
            if (!e.Control && e.KeyCode == Keys.F2) { e.SuppressKeyPress = true; NextBookmark(); return; }
            // Tab/Shift+Tab отстъпва или прибира маркиран блок код.
            if (e.KeyCode == Keys.Tab && SelectionLength > 0) { e.SuppressKeyPress = true; IndentSelection(!e.Shift); return; }
            // Ctrl+Up / Ctrl+Down мести реда, без да го копира.
            if (e.Control && e.KeyCode == Keys.Up) { e.SuppressKeyPress = true; MoveCurrentLine(-1); return; }
            if (e.Control && e.KeyCode == Keys.Down) { e.SuppressKeyPress = true; MoveCurrentLine(1); return; }
            // Ctrl+Shift+W превключва пренасянето на дългите редове.
            if (e.Control && e.Shift && e.KeyCode == Keys.W) { e.SuppressKeyPress = true; ToggleWordWrap(); return; }
            // Ctrl+Alt+T почиства интервалите в края на всички редове.
            if (e.Control && e.Alt && e.KeyCode == Keys.T) { e.SuppressKeyPress = true; TrimTrailingWhitespace(); return; }

            // Ctrl+Space показва локалните допълнения.
            if (e.Control && e.KeyCode == Keys.Space)
            {
                e.SuppressKeyPress = true;
                ShowCompletions();
            }
            // Ctrl+/ коментира или разкоментира избраните редове.
            if (e.Control && e.KeyCode == Keys.OemQuestion)
            {
                e.SuppressKeyPress = true;
                ToggleCommentSelection();
            }
        }

        // Маркира целия ред под курсора, включително крайния нов ред, ако съществува.
        private void SelectCurrentLine()
        {
            int line = GetLineFromCharIndex(SelectionStart);
            int start = GetFirstCharIndexFromLine(line);
            if (start < 0) return;
            int end = line + 1 < Lines.Length ? GetFirstCharIndexFromLine(line + 1) : TextLength;
            Select(start, Math.Max(0, end - start));
            Focus();
        }

        // Показва малък диалог за преход към конкретен ред, без външни библиотеки.
        private void PromptGoToLine()
        {
            using (Form dialog = new Form())
            using (Label label = new Label())
            using (NumericUpDown number = new NumericUpDown())
            using (Button go = new Button())
            using (Button cancel = new Button())
            {
                dialog.Text = "Премини към ред";
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.ClientSize = new Size(270, 105);
                label.Text = "Номер на ред (1–" + Math.Max(1, Lines.Length) + "):";
                label.SetBounds(12, 12, 240, 20);
                number.SetBounds(12, 36, 110, 24);
                number.Minimum = 1;
                number.Maximum = Math.Max(1, Lines.Length);
                number.Value = Math.Min(number.Maximum, GetLineFromCharIndex(SelectionStart) + 1);
                go.Text = "Премини";
                go.DialogResult = DialogResult.OK;
                go.SetBounds(135, 36, 60, 26);
                cancel.Text = "Отказ";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.SetBounds(200, 36, 58, 26);
                dialog.Controls.AddRange(new Control[] { label, number, go, cancel });
                dialog.AcceptButton = go;
                dialog.CancelButton = cancel;
                if (dialog.ShowDialog(FindForm()) == DialogResult.OK) GoToLine((int)number.Value);
            }
        }

        // Вмъква избран шаблон на мястото на курсора или вместо маркирания текст.
        private void InsertTemplate(string template)
        {
            SelectedText = template;
            Focus();
        }

        // Дублира избрания текст или целия ред, ако няма маркиран блок.
        public void DuplicateCurrentLineOrSelection()
        {
            if (SelectionLength > 0)
            {
                string selected = SelectedText;
                int start = SelectionStart;
                SelectedText = selected + selected;
                Select(start + selected.Length, selected.Length);
                return;
            }

            int line = GetLineFromCharIndex(SelectionStart);
            int startIndex = GetFirstCharIndexFromLine(line);
            if (startIndex < 0) return;
            int endIndex = line + 1 < Lines.Length ? GetFirstCharIndexFromLine(line + 1) : TextLength;
            string lineText = Text.Substring(startIndex, endIndex - startIndex);
            Select(endIndex, 0);
            SelectedText = lineText;
            Select(startIndex, lineText.Length);
        }

        // Търси след текущата позиция и продължава от началото при нужда.
        public bool FindNext(string searchText)
        {
            if (String.IsNullOrEmpty(searchText)) return false;
            int start = Math.Min(SelectionStart + SelectionLength, TextLength);
            int found = Find(searchText, start, RichTextBoxFinds.None);
            if (found < 0 && start > 0) found = Find(searchText, 0, RichTextBoxFinds.None);
            if (found >= 0) { Select(found, searchText.Length); ScrollToCaret(); Focus(); return true; }
            return false;
        }

        // Замества всички срещания, като запазва позицията на каретката.
        public int ReplaceAllText(string searchText, string replacementText)
        {
            if (String.IsNullOrEmpty(searchText)) return 0;
            int caret = SelectionStart;
            int count = Regex.Matches(Text, Regex.Escape(searchText), RegexOptions.None).Count;
            Text = Text.Replace(searchText, replacementText ?? String.Empty);
            Select(Math.Min(caret, TextLength), 0);
            return count;
        }

        // Премества курсора към зададен ред, като броенето започва от 1.
        public bool GoToLine(int lineNumber)
        {
            if (lineNumber < 1 || lineNumber > Lines.Length) return false;
            int index = GetFirstCharIndexFromLine(lineNumber - 1);
            if (index < 0) return false;
            Select(index, 0);
            ScrollToCaret();
            Focus();
            return true;
        }

        // Добавя или премахва // за избраните редове.
        public void ToggleCommentSelection()
        {
            int selectionStart = SelectionStart;
            int selectionEnd = SelectionStart + SelectionLength;
            int firstLine = GetLineFromCharIndex(selectionStart);
            int lastLine = GetLineFromCharIndex(Math.Max(selectionStart, selectionEnd - 1));
            int start = GetFirstCharIndexFromLine(firstLine);
            int end = lastLine + 1 < Lines.Length ? GetFirstCharIndexFromLine(lastLine + 1) : TextLength;
            string block = Text.Substring(start, end - start);
            string[] lines = block.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            bool remove = lines.Where(line => !String.IsNullOrWhiteSpace(line)).All(line => line.TrimStart().StartsWith("//"));
            for (int i = 0; i < lines.Length; i++)
            {
                if (String.IsNullOrWhiteSpace(lines[i])) continue;
                int indent = lines[i].Length - lines[i].TrimStart().Length;
                if (remove) lines[i] = lines[i].Remove(indent, 2).Insert(indent, "");
                else lines[i] = lines[i].Insert(indent, "//");
            }
            string result = String.Join(Environment.NewLine, lines);
            Select(start, end - start);
            SelectedText = result;
            Select(start, Math.Min(result.Length, TextLength - start));
        }

        private void ShowCompletions()
        {
            // Списъкът съдържа често използвани Arduino/C++ функции, типове и готови конструкции.
            _completionMenu.Items.Clear();
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "setup", "void setup()\\r\\n{\\r\\n    // Инициализация\\r\\n}\\r\\n" },
                { "loop", "void loop()\\r\\n{\\r\\n    // Основен цикъл\\r\\n}\\r\\n" },
                { "if", "if (условие)\\r\\n{\\r\\n    \\r\\n}\\r\\n" },
                { "for", "for (int i = 0; i < 10; i++)\\r\\n{\\r\\n    \\r\\n}\\r\\n" },
                { "while", "while (условие)\\r\\n{\\r\\n    \\r\\n}\\r\\n" },
                { "switch", "switch (стойност)\\r\\n{\\r\\n    case 0:\\r\\n        break;\\r\\n    default:\\r\\n        break;\\r\\n}\\r\\n" },
                { "pinMode", "pinMode(LED_BUILTIN, OUTPUT);" },
                { "digitalWrite", "digitalWrite(LED_BUILTIN, HIGH);" },
                { "digitalRead", "digitalRead(LED_BUILTIN)" },
                { "analogRead", "analogRead(A0)" },
                { "analogWrite", "analogWrite(LED_BUILTIN, 128);" },
                { "delay", "delay(1000);" },
                { "delayMicroseconds", "delayMicroseconds(10);" },
                { "millis", "millis()" },
                { "micros", "micros()" },
                { "Serial.begin", "Serial.begin(9600);" },
                { "Serial.print", "Serial.print(стойност);" },
                { "Serial.println", "Serial.println(стойност);" },
                { "attachInterrupt", "attachInterrupt(digitalPinToInterrupt(2), функция, RISING);" },
                { "HIGH", "HIGH" }, { "LOW", "LOW" }, { "INPUT", "INPUT" },
                { "OUTPUT", "OUTPUT" }, { "INPUT_PULLUP", "INPUT_PULLUP" },
                { "LED_BUILTIN", "LED_BUILTIN" }, { "true", "true" }, { "false", "false" },
                { "bool", "bool" }, { "byte", "byte" }, { "char", "char" }, { "int", "int" },
                { "long", "long" }, { "float", "float" }, { "double", "double" },
                { "String", "String" }, { "unsigned long", "unsigned long" }, { "const", "const" },
                { "volatile", "volatile" }, { "return", "return" }, { "PROGMEM", "PROGMEM" }
            };

            // Определяме думата пред курсора и показваме първо съвпадащите допълнения.
            int caretIndex = SelectionStart;
            int prefixStart = caretIndex;
            while (prefixStart > 0 && IsIdentifierChar(Text[prefixStart - 1])) prefixStart--;
            string prefix = Text.Substring(prefixStart, caretIndex - prefixStart);
            foreach (var option in options.Where(item => String.IsNullOrEmpty(prefix) ||
                item.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                string label = option.Key;
                string insertion = option.Value;
                _completionMenu.Items.Add(label, null, (sender, args) =>
                {
                    // Заменяме написания префикс, вместо да дублираме текста пред курсора.
                    Select(prefixStart, caretIndex - prefixStart);
                    SelectedText = insertion;
                    Focus();
                });
            }

            if (_completionMenu.Items.Count == 0)
            {
                _completionMenu.Items.Add("Няма съвпадения", null, null).Enabled = false;
            }
            Point caret = GetPositionFromCharIndex(SelectionStart);
            _completionMenu.Show(this, caret);
        }

        private void EditorMouseDown(object sender, MouseEventArgs e)
        {
            // При десен бутон поставяме каретката на мястото на клика, ако няма маркиран текст.
            if (e.Button == MouseButtons.Right && SelectionLength == 0)
            {
                int clicked = GetCharIndexFromPosition(e.Location);
                if (clicked >= 0 && clicked <= TextLength) Select(clicked, 0);
                return;
            }
            // Ctrl+щракване върху име на функция търси тялото ѝ в текущия файл.
            if (e.Button != MouseButtons.Left || (ModifierKeys & Keys.Control) == 0) return;
            int index = GetCharIndexFromPosition(e.Location);
            if (index < 0 || index >= TextLength) return;
            string word = GetWordAt(index);
            if (String.IsNullOrEmpty(word)) return;
            NavigateToDefinition(word);
        }

        // Навигира към дефиниция на функция или към първа декларация на идентификатора.
        private bool NavigateToDefinition(string word)
        {
            if (String.IsNullOrEmpty(word)) return false;
            string escaped = Regex.Escape(word);
            Match definition = Regex.Match(Text,
                @"(?m)^[ \t]*(?:(?:static|inline|virtual|constexpr|extern)\s+)*(?:[\w:~*&<>]+[ \t]+)+" +
                escaped + @"[ \t]*\([^;{}]*\)[ \t]*(?:const[ \t]*)?(?:\{|\r?\n[ \t]*\{)");
            if (!definition.Success)
            {
                // Резервен вариант за променливи, макроси и декларации.
                definition = Regex.Match(Text, @"(?m)^[ \t]*(?:#\s*define\s+)?" + escaped + @"\b");
            }
            if (!definition.Success) return false;
            Select(definition.Index, 0);
            ScrollToCaret();
            Focus();
            return true;
        }

        // Използва се от контекстното меню „Към дефиницията“.
        public void NavigateFromCaret()
        {
            string word = GetWordAt(SelectionStart);
            if (!NavigateToDefinition(word))
                MessageBox.Show(this, "Не е намерена дефиниция на този идентификатор в текущия файл.", "Навигация в кода",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // Съобщава на главния прозорец, че трябва да отвори диалога за търсене.
        public event EventHandler FindRequested;
        private void RequestFind() { FindRequested?.Invoke(this, EventArgs.Empty); }

        private void EditorMouseMove(object sender, MouseEventArgs e)
        {
            // При скролиране само рестартираме таймера; не търсим думи и не сканираме текста.
            _lastMousePoint = e.Location;
            _hintTimer.Stop();
            _hintTimer.Start();
            if (_hintVisible)
            {
                _hint.Hide(this);
                _hintVisible = false;
            }
        }

        // Изпълнява се само след 2 секунди неподвижна мишка.
        private void ShowHintForCurrentMousePosition()
        {
            if (IsDisposed || !IsHandleCreated) return;
            int index = GetCharIndexFromPosition(_lastMousePoint);
            if (index < 0 || index >= TextLength)
            {
                HideHint();
                return;
            }

            string word = GetWordAt(index);
            if (String.Equals(word, _lastHintWord, StringComparison.Ordinal)) return;

            string tip = BuildHint(word);
            _lastHintWord = word;
            _lastHintText = tip;
            if (String.IsNullOrEmpty(tip))
            {
                HideHint();
                return;
            }

            _hint.Show(tip, this, _lastMousePoint.X + 14, _lastMousePoint.Y + 18, 10000);
            _hintVisible = true;
        }

        // Скриваме подсказката и изчистваме кеша само при реална промяна на думата.
        private void HideHint()
        {
            _hintTimer.Stop();
            if (_hintVisible) _hint.Hide(this);
            _hintVisible = false;
            _lastHintWord = null;
            _lastHintText = null;
        }

        private string GetWordAt(int index)
        {
            // Намираме границите на C/C++ идентификатора около курсора.
            int start = Math.Min(index, TextLength - 1);
            int end = start;
            while (start > 0 && IsIdentifierChar(Text[start - 1])) start--;
            while (end < TextLength && IsIdentifierChar(Text[end])) end++;
            return end > start ? Text.Substring(start, end - start) : "";
        }

        private bool IsIdentifierChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private string BuildHint(string word)
        {
            // Кратки описания за често използваните Arduino функции и типове.
            var docs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {"setup", "void setup()\nИзвиква се еднократно след стартиране или reset."},
                {"loop", "void loop()\nИзвиква се многократно, докато платката работи."},
                {"pinMode", "pinMode(pin, mode)\nНастройва пина като INPUT, OUTPUT или INPUT_PULLUP."},
                {"digitalWrite", "digitalWrite(pin, HIGH/LOW)\nЗадава логическо ниво на цифров изход."},
                {"digitalRead", "digitalRead(pin) -> HIGH/LOW\nПрочита цифров вход."},
                {"analogRead", "analogRead(pin) -> int\nПрочита аналогов вход; диапазонът зависи от платката."},
                {"analogWrite", "analogWrite(pin, value)\nИзвежда PWM сигнал на поддържан пин."},
                {"delay", "delay(milliseconds)\nИзчаква зададения брой милисекунди."},
                {"millis", "millis() -> unsigned long\nМилисекунди от стартиране."},
                {"micros", "micros() -> unsigned long\nМикросекунди от стартиране."},
                {"Serial", "Serial\nОбект за сериен обмен; инициализира се със Serial.begin(скорост)."},
                {"begin", "begin(скорост)\nИнициализация; точната сигнатура зависи от типа на обекта."},
                {"HIGH", "HIGH\nЛогическо високо ниво."},{"LOW", "LOW\nЛогическо ниско ниво."},
                {"INPUT", "INPUT\nРежим на цифров вход."},{"OUTPUT", "OUTPUT\nРежим на цифров изход."},
                {"bool", "bool\nБулев тип: true или false."},{"byte", "byte\n8-битова беззнакова стойност в Arduino."},
                {"int", "int\nЦелочислен тип; размерът зависи от архитектурата."},
                {"float", "float\nЧисло с плаваща запетая."},
                {"String", "String\nArduino клас за текст; при малко RAM обмислете char масив."}
            };
            if (String.IsNullOrEmpty(word)) return null;
            if (docs.ContainsKey(word)) return docs[word];
            // Не сканираме целия файл при всяко движение на мишката.
            // Такова търсене забавя скролиране при големи скечове; дефинициите
            // могат да се намерят надеждно с Ctrl+щракване или командата от менюто.
            if (_keywords.Contains(word)) return "Ключова дума или често използван Arduino символ: " + word;
            return null;
        }

        // Премества текущия ред нагоре или надолу, като запазва позицията на каретката.
        public void MoveCurrentLine(int direction)
        {
            int line = GetLineFromCharIndex(SelectionStart);
            int target = line + direction;
            if (target < 0 || target >= Lines.Length) return;
            int firstLine = Math.Min(line, target);
            int start = GetFirstCharIndexFromLine(firstLine);
            int endLine = Math.Max(line, target);
            int end = endLine + 1 < Lines.Length ? GetFirstCharIndexFromLine(endLine + 1) : TextLength;
            if (start < 0 || end < start) return;
            string[] pair = Text.Substring(start, end - start).Replace("\r\n", "\n").Split('\n');
            if (pair.Length < 2) return;
            string first = pair[0], second = pair[1];
            string swapped = direction < 0
                ? second + Environment.NewLine + first
                : second + Environment.NewLine + first;
            // При движение нагоре първо е редът отдолу; при движение надолу първо е редът отгоре.
            if (direction < 0) swapped = second + Environment.NewLine + first;
            else swapped = second + Environment.NewLine + first;
            Select(start, end - start);
            SelectedText = swapped + (pair.Length > 2 ? Environment.NewLine + String.Join(Environment.NewLine, pair.Skip(2).ToArray()) : "");
            Select(start, 0);
            ScrollToCaret();
        }

        // Премахва интервалите и табулациите в края на всеки ред, без да променя отстъпите.
        public void TrimTrailingWhitespace()
        {
            int caret = SelectionStart;
            int selectionLength = SelectionLength;
            string[] lines = Text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd(' ', '\t', '\r');
            string cleaned = String.Join(Environment.NewLine, lines);
            Text = cleaned;
            Select(Math.Min(caret, TextLength), Math.Min(selectionLength, Math.Max(0, TextLength - Math.Min(caret, TextLength))));
        }

        // Сортира азбучно избраните редове; ако няма избор, обработва текущия ред.
        public void SortSelectedLines()
        {
            int selectionStart = SelectionStart;
            int selectionEnd = SelectionStart + SelectionLength;
            int first = GetLineFromCharIndex(selectionStart);
            int last = GetLineFromCharIndex(Math.Max(selectionStart, selectionEnd - 1));
            int start = GetFirstCharIndexFromLine(first);
            int end = last + 1 < Lines.Length ? GetFirstCharIndexFromLine(last + 1) : TextLength;
            if (start < 0 || end < start) return;
            string block = Text.Substring(start, end - start);
            string[] lines = block.Replace("\r\n", "\n").Split('\n');
            Array.Sort(lines, StringComparer.OrdinalIgnoreCase);
            string sorted = String.Join(Environment.NewLine, lines);
            Select(start, end - start);
            SelectedText = sorted;
            Select(start, Math.Min(sorted.Length, TextLength - start));
        }

        // Преобразува само маркирания текст, без да засяга останалия код.
        public void ChangeSelectionCase(bool upper)
        {
            if (SelectionLength == 0) return;
            string selected = SelectedText;
            int start = SelectionStart;
            SelectedText = upper ? selected.ToUpperInvariant() : selected.ToLowerInvariant();
            Select(start, Math.Min(SelectedText.Length, TextLength - start));
        }

        // Вмъква текущата дата и час като текст, удобен за коментар в кода.
        public void InsertDateTime()
        {
            SelectedText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }


        // Поставя или маха отметка на текущия ред; отметките са отделни за всеки отворен файл.
        public event EventHandler BookmarksChanged;
        public bool IsBookmarked(int zeroBasedLine) { return _bookmarks.Contains(zeroBasedLine); }

        public void ToggleBookmark()
        {
            int line = GetLineFromCharIndex(SelectionStart);
            if (!_bookmarks.Add(line)) _bookmarks.Remove(line);
            _bookmarks.RemoveWhere(n => n < 0 || n >= Lines.Length);
            BookmarksChanged?.Invoke(this, EventArgs.Empty);
        }

        // Преминава към следващата отметка и започва от първата след края.
        public bool NextBookmark()
        {
            _bookmarks.RemoveWhere(n => n < 0 || n >= Lines.Length);
            if (_bookmarks.Count == 0) { MessageBox.Show(this, "Няма отметки в този файл.", "Отметки", MessageBoxButtons.OK, MessageBoxIcon.Information); return false; }
            int current = GetLineFromCharIndex(SelectionStart);
            int next = _bookmarks.Where(n => n > current).DefaultIfEmpty(_bookmarks.Min()).Min();
            return GoToLine(next + 1);
        }

        // Изтрива всички отметки в текущия редактор.
        public void ClearBookmarks() { _bookmarks.Clear(); BookmarksChanged?.Invoke(this, EventArgs.Empty); }

        // Отстъпва или прибира маркираните редове, без да променя останалия текст.
        public void IndentSelection(bool indent)
        {
            int startPos = SelectionStart, selectionEnd = SelectionStart + SelectionLength;
            int first = GetLineFromCharIndex(startPos);
            int last = GetLineFromCharIndex(Math.Max(startPos, selectionEnd - 1));
            int start = GetFirstCharIndexFromLine(first);
            int end = last + 1 < Lines.Length ? GetFirstCharIndexFromLine(last + 1) : TextLength;
            if (start < 0 || end < start) return;
            string[] lines = Text.Substring(start, end - start).Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (indent) lines[i] = "    " + lines[i];
                else if (lines[i].StartsWith("    ")) lines[i] = lines[i].Substring(4);
                else if (lines[i].StartsWith("\t")) lines[i] = lines[i].Substring(1);
            }
            string result = String.Join(Environment.NewLine, lines);
            Select(start, end - start); SelectedText = result;
            Select(Math.Min(startPos + (indent ? 4 : 0), TextLength), Math.Min(selectionEnd - startPos, Math.Max(0, TextLength - startPos)));
        }

        // Обгръща маркиран код с блоков коментар или премахва вече поставените ограничители.
        public void ToggleBlockComment()
        {
            int start = SelectionStart, length = SelectionLength;
            if (length == 0) { MessageBox.Show(this, "Първо маркирайте блока код.", "Блоков коментар", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            string selected = SelectedText;
            if (selected.StartsWith("/*") && selected.EndsWith("*/") && selected.Length >= 4)
                SelectedText = selected.Substring(2, selected.Length - 4);
            else SelectedText = "/*" + selected + "*/";
            Select(start, Math.Min(TextLength - start, length + 4));
        }

        // Превключва визуалното пренасяне на дългите редове.
        public void ToggleWordWrap()
        {
            WordWrap = !WordWrap;
            ScrollToCaret();
        }

        // Променя размера на шрифта в разумни граници.
        public void ChangeEditorFontSize(float delta)
        {
            float size = Math.Max(7.0f, Math.Min(24.0f, Font.Size + delta));
            Font = new Font(Font.FontFamily, size, Font.Style);
        }

        // Връща стандартния размер на шрифта.
        public void ResetEditorFontSize()
        {
            Font = new Font("Consolas", 10.0f);
        }
    }
}