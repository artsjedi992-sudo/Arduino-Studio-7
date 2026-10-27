using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ArduinoStudio7
{
    // Редактор с тъмен изглед, оцветяване и локални подсказки без външни библиотеки.
    public sealed class CodeEditor : RichTextBox
    {
        // Win32 съобщения за запазване на видимия ред при оцветяване.
        private const int WM_SETREDRAW = 0x000B;
        private const int EM_GETFIRSTVISIBLELINE = 0x00CE;
        private const int EM_LINESCROLL = 0x00B6;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly ToolTip _hint = new ToolTip();
        private readonly Timer _highlightTimer = new Timer();
        private readonly ContextMenuStrip _completionMenu = new ContextMenuStrip();
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
            BackColor = Color.White;
            ForeColor = Color.FromArgb(30, 30, 30);
            BorderStyle = BorderStyle.None;
            AcceptsTab = true;
            WordWrap = false;
            DetectUrls = false;
            HideSelection = false;
            // Отлагаме оцветяването, за да остане въвеждането плавно.
            _highlightTimer.Interval = 350;
            _highlightTimer.Tick += delegate { _highlightTimer.Stop(); HighlightSyntax(); };
            TextChanged += delegate { _highlightTimer.Stop(); _highlightTimer.Start(); };
            MouseMove += EditorMouseMove;
            MouseDown += EditorMouseDown;
            KeyDown += EditorKeyDown;
            Disposed += delegate { _hint.Dispose(); _highlightTimer.Dispose(); _completionMenu.Dispose(); };
        }

        private void HighlightSyntax()
        {
            // Запазваме каретката и най-горния видим ред, за да не подскача редакторът.
            if (TextLength == 0 || !IsHandleCreated) return;
            int caret = SelectionStart;
            int selectionLength = SelectionLength;
            int firstVisibleLine = (int)SendMessage(Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
            SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                // Оцветяването променя само форматирането, а не самия текст.
                SelectAll();
                SelectionColor = Color.FromArgb(30, 30, 30);
                ApplyColor(@"//[^\r\n]*|/\*[\s\S]*?\*/", Color.FromArgb(0, 128, 0));
                ApplyColor(@"(?:""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*')", Color.FromArgb(163, 21, 21));
                ApplyColor(@"\b\d+(?:\.\d+)?\b", Color.FromArgb(9, 134, 88));

                string source = Text;
                foreach (Match match in Regex.Matches(source, @"\b[A-Za-z_][A-Za-z0-9_]*\b"))
                {
                    string word = match.Value;
                    if (_keywords.Contains(word))
                    {
                        Select(match.Index, match.Length);
                        SelectionColor = Color.FromArgb(0, 0, 255);
                    }
                    else if (Regex.IsMatch(source.Substring(match.Index + match.Length), @"^\s*\("))
                    {
                        Select(match.Index, match.Length);
                        SelectionColor = Color.FromArgb(121, 94, 38);
                    }
                }
                // Връщаме избора и позицията на курсора, без да принуждаваме скрола към тях.
                Select(Math.Min(caret, TextLength), Math.Min(selectionLength, Math.Max(0, TextLength - caret)));
                SelectionColor = Color.FromArgb(30, 30, 30);
            }
            finally
            {
                SendMessage(Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                int currentLine = (int)SendMessage(Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
                SendMessage(Handle, EM_LINESCROLL, IntPtr.Zero, new IntPtr(firstVisibleLine - currentLine));
                Invalidate();
            }
        }

        private void ApplyColor(string pattern, Color color)
        {
            foreach (Match match in Regex.Matches(Text, pattern))
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
            string[] lines = block.Split(new[] { "\\r\\n", "\\n" }, StringSplitOptions.None);
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
            // Показваме допълненията като меню до текущата позиция на каретката.
            _completionMenu.Items.Clear();
            string[] options = { "setup", "loop", "pinMode", "digitalWrite", "digitalRead", "analogRead",
                "analogWrite", "delay", "millis", "micros", "Serial.begin", "Serial.println",
                "HIGH", "LOW", "INPUT", "OUTPUT", "INPUT_PULLUP", "LED_BUILTIN", "bool", "byte", "int", "long", "float", "String" };
            foreach (string option in options)
            {
                string value = option;
                _completionMenu.Items.Add(value, null, (s, e) =>
                {
                    // Вмъкваме избраната дума/функция на мястото на каретката.
                    SelectedText = value;
                    Focus();
                });
            }
            Point caret = GetPositionFromCharIndex(SelectionStart);
            _completionMenu.Show(this, caret);
        }

        private void EditorMouseDown(object sender, MouseEventArgs e)
        {
            // Ctrl+щракване върху идентификатор премества каретката към локалната дефиниция.
            if (e.Button != MouseButtons.Left || (ModifierKeys & Keys.Control) == 0) return;
            int index = GetCharIndexFromPosition(e.Location);
            if (index < 0 || index >= TextLength) return;
            string word = GetWordAt(index);
            if (String.IsNullOrEmpty(word)) return;
            Match definition = Regex.Match(Text,
                @"(?m)^\\s*(?:(?:static\\s+|inline\\s+|const\\s+|unsigned\\s+)*(?:void|bool|boolean|byte|char|short|int|long|float|double|String|size_t|[A-Z]\\w*)\\s+)?"
                + Regex.Escape(word) + @"\\s*(?:\\([^;{}]*\\)\\s*(?:const\\s*)?\\{|[=;,)])");
            if (!definition.Success) return;
            Select(definition.Index, 0);
            ScrollToCaret();
            Focus();
        }

        private void EditorMouseMove(object sender, MouseEventArgs e)
        {
            // Определяме думата под курсора и показваме справка, когато е налична.
            int index = GetCharIndexFromPosition(e.Location);
            if (index < 0 || index >= TextLength) return;
            string word = GetWordAt(index);
            string tip = BuildHint(word);
            if (tip == null) { _hint.Hide(this); return; }
            _hint.Show(tip, this, e.Location.X + 14, e.Location.Y + 18, 4000);
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
            // Търсим локална декларация на функция, променлива, тип или макрос.
            Match declaration = Regex.Match(Text,
                @"(?m)^\s*(?:#\s*define\s+|typedef\s+\w+\s+|(?:const\s+)?(?:static\s+)?(?:unsigned\s+)?(?:void|bool|boolean|byte|char|short|int|long|float|double|String|size_t|[A-Z]\w*)\s+)" +
                Regex.Escape(word) + @"\b[^;\r\n]*(?:\{|;)?");
            if (declaration.Success) return "Дефиниция в текущия файл:\n" + declaration.Value.Trim();
            if (_keywords.Contains(word)) return "Ключова дума или често използван Arduino символ: " + word;
            return null;
        }
    }
}