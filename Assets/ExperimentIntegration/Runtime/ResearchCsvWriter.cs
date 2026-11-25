using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WalkingEyeExperiment
{
    public sealed class ResearchCsvWriter : IDisposable
    {
        readonly StreamWriter writer;
        readonly string[] columns;
        public ResearchCsvWriter(string path, string[] columns)
        {
            this.columns=columns;
            writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            writer.WriteLine(string.Join(",", columns));
            writer.Flush();
        }
        public static string Escape(string value)
        {
            value = value ?? "";
            // Keep user metadata from becoming a spreadsheet formula when the CSV is opened.
            if (value.Length > 0 && "=+@".IndexOf(value[0]) >= 0) value="'"+value;
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        public void Write(IReadOnlyDictionary<string,string> values)
        {
            var cells = new string[columns.Length];
            for (int i=0; i<columns.Length; i++) cells[i]=Escape(values.TryGetValue(columns[i], out var value) ? value : "");
            writer.WriteLine(string.Join(",", cells));
        }
        public void Flush() { writer.Flush(); }
        public void Dispose() { writer.Dispose(); }
    }
}
