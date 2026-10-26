using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ArduinoStudio7
{
    // Редактор с тъмен изглед, оцветяване и локални подсказки без външни библиотеки.
    public sealed class CodeEditor : RichTextBox
    {
        private readonly ToolTip _hint = new ToolTip();
        private readonly Timer _highlightTimer = new Timer();
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
            BackColor = Color.FromArgb(30, 30, 30);
            ForeColor = Color.FromArgb(220, 220, 220);
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
            Disposed += delegate { _hint.Dispose(); _highlightTimer.Dispose(); };
        }

        private void HighlightSyntax()
        {
            // Запазваме позицията на каретката и не променяме текста.
            int caret = SelectionStart;
            if (TextLength == 0) return;
            SelectAll();
            SelectionColor = Color.FromArgb(220, 220, 220);
            // Оцветяваме коментарите, низовете и числата.
            ApplyColor(@"//[^\r\n]*|/\*[\s\S]*?\*/", Color.FromArgb(106, 153, 85));
            ApplyColor(@"(?:""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*')", Color.FromArgb(206, 145, 120));
            ApplyColor(@"\b\d+(?:\.\d+)?\b", Color.FromArgb(181, 206, 168));
            // Оцветяваме ключовите думи и имената, след които следва скоба за извикване.
            foreach (Match match in Regex.Matches(Text, @"\b[A-Za-z_][A-Za-z0-9_]*\b"))
            {
                string word = match.Value;
                if (_keywords.Contains(word))
                {
                    Select(match.Index, match.Length);
                    SelectionColor = Color.FromArgb(86, 156, 214);
                }
                else if (Regex.IsMatch(Text.Substring(match.Index + match.Length), @"^\s*\("))
                {
                    Select(match.Index, match.Length);
                    SelectionColor = Color.FromArgb(220, 220, 170);
                }
            }
            Select(Math.Min(caret, TextLength), 0);
            SelectionColor = Color.FromArgb(220, 220, 220);
        }

        private void ApplyColor(string pattern, Color color)
        {
            foreach (Match match in Regex.Matches(Text, pattern))
            {
                Select(match.Index, match.Length);
                SelectionColor = color;
            }
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