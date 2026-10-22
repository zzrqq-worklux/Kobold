using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Kobold.Core
{
    /// <summary>
    /// Creation rules for the new-file dialog. Office extensions get a minimal
    /// OOXML package so Word/Excel/PowerPoint open the result without a repair
    /// prompt; PowerPoint has no scriptable minimal form we can write by hand,
    /// so it comes from the embedded blank presentation. Nothing here ever
    /// overwrites an existing file or folder.
    /// </summary>
    public static class FileTemplates
    {
        private const string BlankPptxResource = "Kobold.Resources.blank.pptx";

        private const string DocxContentTypes =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "</Types>";

        private const string DocxRels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "</Relationships>";

        private const string DocxDocument =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            "<w:body><w:p/></w:body></w:document>";

        private const string XlsxContentTypes =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "</Types>";

        private const string XlsxRels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>";

        private const string XlsxWorkbook =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";

        private const string XlsxWorkbookRels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "</Relationships>";

        private const string XlsxSheet =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<sheetData/></worksheet>";

        /// <summary>True for the extensions that need an OOXML package instead of an empty file.</summary>
        public static bool IsOfficeExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return false;
            return string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".pptx", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Creates the file; never overwrites. False (with a message) on any failure.</summary>
        public static bool TryCreate(string path, out string error)
        {
            error = null;
            try
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    error = "A file or folder with that name already exists.";
                    return false;
                }

                string extension = Path.GetExtension(path) ?? "";
                if (IsOfficeExtension(extension))
                {
                    byte[] package = BuildOfficePackage(extension);
                    if (package == null)
                    {
                        error = "The " + extension + " template is missing from the application.";
                        return false;
                    }
                    File.WriteAllBytes(path, package);
                    return true;
                }

                using (new FileStream(path, FileMode.CreateNew)) { }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>The bytes of the minimal package for a docx/xlsx/pptx name, or null when it cannot be built.</summary>
        public static byte[] BuildOfficePackage(string extension)
        {
            string ext = (extension ?? "").ToLowerInvariant();
            if (ext == ".pptx") return ReadBlankPptx();
            if (ext != ".docx" && ext != ".xlsx") return null;

            try
            {
                using (var stream = new MemoryStream())
                {
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                    {
                        if (ext == ".docx")
                        {
                            AddEntry(zip, "[Content_Types].xml", DocxContentTypes);
                            AddEntry(zip, "_rels/.rels", DocxRels);
                            AddEntry(zip, "word/document.xml", DocxDocument);
                        }
                        else
                        {
                            AddEntry(zip, "[Content_Types].xml", XlsxContentTypes);
                            AddEntry(zip, "_rels/.rels", XlsxRels);
                            AddEntry(zip, "xl/workbook.xml", XlsxWorkbook);
                            AddEntry(zip, "xl/_rels/workbook.xml.rels", XlsxWorkbookRels);
                            AddEntry(zip, "xl/worksheets/sheet1.xml", XlsxSheet);
                        }
                    }
                    return stream.ToArray();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The embedded PowerPoint stub; PowerPoint files cannot be hand-written as cheaply.</summary>
        private static byte[] ReadBlankPptx()
        {
            try
            {
                using (var stream = typeof(FileTemplates).Assembly.GetManifestResourceStream(BlankPptxResource))
                {
                    if (stream == null) return null;
                    using (var buffer = new MemoryStream())
                    {
                        stream.CopyTo(buffer);
                        return buffer.ToArray();
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void AddEntry(ZipArchive zip, string name, string xml)
        {
            var entry = zip.CreateEntry(name);
            using (var stream = entry.Open())
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(xml);
            }
        }
    }
}
