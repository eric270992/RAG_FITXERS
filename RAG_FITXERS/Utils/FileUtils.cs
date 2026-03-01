using DocumentFormat.OpenXml.Packaging;
using System;
using System.Collections.Generic;
using System.Text;
using UglyToad.PdfPig;

namespace RAG_FITXERS.Utils
{
    public static class FileUtils
    {
        public static string CalculateHash(string filePath)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = sha256.ComputeHash(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        public static async Task<string> ExtractAsync(string path)
        {
            string ext = Path.GetExtension(path).ToLower();
            return ext switch
            {
                ".pdf" => ExtractPdf(path),
                ".docx" => ExtractDocx(path),
                ".txt" => await File.ReadAllTextAsync(path),
                _ => throw new NotSupportedException("Format no suportat")
            };
        }

        private static string ExtractPdf(string path)
        {
            using var pdf = PdfDocument.Open(path);
            return string.Join("\n", pdf.GetPages().Select(p => p.Text));
        }

        private static string ExtractDocx(string path)
        {
            using var doc = WordprocessingDocument.Open(path, false);
            return doc.MainDocumentPart.Document.Body.InnerText;
        }
    }
}
