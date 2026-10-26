using System;
using System.Windows.Forms;
namespace ArduinoStudio7
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // Включваме стандартните визуални стилове на Windows Forms.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Стартираме главния прозорец на програмата.
            Application.Run(new MainForm());
        }
    }
}