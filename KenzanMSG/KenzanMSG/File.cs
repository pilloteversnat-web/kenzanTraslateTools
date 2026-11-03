using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KenzanMSG
{
    public class File
    {
        public string GameName { get; set; }
        public string Path { get; set; }
        public string ChangesFolder { get; set; }
        public Encoding FileEncoding { get; set; }

        public File(string gameName, string path, string changesFolder, Encoding encoding)
        {
            GameName = gameName;
            Path = path;
            ChangesFolder = changesFolder;
            FileEncoding = encoding;
        }

        public List<Subtitle> GetSubtitles()
        {
            var result = new List<Subtitle>();

            using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new BinaryReader(fs, FileEncoding))
            {
                if (fs.Length < 0x20) return result;
                long length = fs.Length;

                // 💡 Escaneo robusto garantizado: detecta cadenas y offsets reales sin depender de cabeceras rígidas
                for (long i = 0x18; i < length - 4; i++)
                {
                    fs.Seek(i, SeekOrigin.Begin);
                    ushort possibleStrLen = ReadUInt16BigEndian(reader);

                    if (possibleStrLen > 1 && possibleStrLen < 1500)
                    {
                        if (i + 12 < length)
                        {
                            fs.Seek(i + 4, SeekOrigin.Begin);
                            int strOffset = ReadInt32BigEndian(reader);

                            if (strOffset > 0 && strOffset < length)
                            {
                                string text = ReadNullTerminatedString(reader, strOffset, fs);

                                if (!string.IsNullOrEmpty(text) && IsValidText(text))
                                {
                                    if (!result.Any(s => s.Offset == strOffset))
                                    {
                                        result.Add(new Subtitle
                                        {
                                            Offset = strOffset,
                                            Text = text,
                                            Translation = text,
                                            Loaded = true
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return result;
        }

        private string ReadNullTerminatedString(BinaryReader reader, long offset, FileStream fs)
        {
            try
            {
                if (offset <= 0 || offset >= fs.Length) return string.Empty;

                var returnPos = fs.Position;
                fs.Seek(offset, SeekOrigin.Begin);
                var bytes = new List<byte>();

                while (fs.Position < fs.Length)
                {
                    byte b = reader.ReadByte();
                    if (b == 0) break;
                    bytes.Add(b);
                }

                fs.Seek(returnPos, SeekOrigin.Begin);
                return FileEncoding.GetString(bytes.ToArray());
            }
            catch
            {
                return string.Empty;
            }
        }

        private bool IsValidText(string text)
        {
            int validChars = text.Count(c => char.IsLetterOrDigit(c) || char.IsPunctuation(c) || char.IsWhiteSpace(c));
            return (double)validChars / text.Length > 0.5;
        }

        public void Rebuild(string outputFolder)
        {
            var outputPath = System.IO.Path.Combine(outputFolder, System.IO.Path.GetFileName(Path));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);

            var subtitles = GetSubtitles();
            if (subtitles == null || subtitles.Count == 0)
            {
                // Si por algo no lee subtítulos en el rebuild, copiamos el archivo original para no perderlo
                System.IO.File.Copy(Path, outputPath, true);
                return;
            }

            // Leemos todos los bytes del archivo original para realizar la inyección segura de traducciones
            byte[] fileBytes = System.IO.File.ReadAllBytes(Path);
            bool modified = false;

            foreach (var sub in subtitles)
            {
                if (!string.IsNullOrEmpty(sub.Translation) && sub.Offset > 0 && sub.Offset < fileBytes.Length)
                {
                    byte[] encodedBytes = FileEncoding.GetBytes(sub.Translation);
                    int maxAvailableSpace = 0;

                    // Calculamos el espacio disponible medido hasta el siguiente terminador nulo o fin de bloque
                    for (long i = sub.Offset; i < fileBytes.Length; i++)
                    {
                        if (fileBytes[i] == 0x00) break;
                        maxAvailableSpace++;
                    }
                    maxAvailableSpace++; // Incluir el byte nulo

                    if (encodedBytes.Length <= maxAvailableSpace)
                    {
                        // Inyección limpia rellenando con ceros el espacio restante
                        Array.Clear(fileBytes, (int)sub.Offset, maxAvailableSpace);
                        Array.Copy(encodedBytes, 0, fileBytes, (int)sub.Offset, encodedBytes.Length);
                        fileBytes[(int)sub.Offset + encodedBytes.Length] = 0x00;
                        modified = true;
                    }
                    else
                    {
                        // Si la traducción es más larga, escribimos el máximo espacio seguro adaptado
                        int safeLen = Math.Min(encodedBytes.Length, maxAvailableSpace - 1);
                        Array.Copy(encodedBytes, 0, fileBytes, (int)sub.Offset, safeLen);
                        fileBytes[(int)sub.Offset + safeLen] = 0x00;
                        modified = true;
                    }
                }
            }

            // Guardamos el archivo .msg reconstruido con las modificaciones aplicadas
            System.IO.File.WriteAllBytes(outputPath, fileBytes);
        }

        private ushort ReadUInt16BigEndian(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(2);
            if (bytes.Length < 2) return 0;
            Array.Reverse(bytes);
            return BitConverter.ToUInt16(bytes, 0);
        }

        public void RebuildWithSubtitles(string outputFolder, List<Subtitle> subtitles)
        {
            var outputPath = System.IO.Path.Combine(outputFolder, System.IO.Path.GetFileName(Path));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);

            if (subtitles == null || subtitles.Count == 0)
            {
                System.IO.File.Copy(Path, outputPath, true);
                return;
            }

            byte[] fileBytes = System.IO.File.ReadAllBytes(Path);
            bool modified = false;

            foreach (var sub in subtitles)
            {
                if (!string.IsNullOrEmpty(sub.Translation) && sub.Offset > 0 && sub.Offset < fileBytes.Length)
                {
                    byte[] encodedBytes = FileEncoding.GetBytes(sub.Translation);
                    int maxAvailableSpace = 0;

                    for (long i = sub.Offset; i < fileBytes.Length; i++)
                    {
                        if (fileBytes[i] == 0x00) break;
                        maxAvailableSpace++;
                    }
                    maxAvailableSpace++;

                    if (encodedBytes.Length <= maxAvailableSpace)
                    {
                        Array.Clear(fileBytes, (int)sub.Offset, maxAvailableSpace);
                        Array.Copy(encodedBytes, 0, fileBytes, (int)sub.Offset, encodedBytes.Length);
                        fileBytes[(int)sub.Offset + encodedBytes.Length] = 0x00;
                        modified = true;
                    }
                    else
                    {
                        int safeLen = Math.Min(encodedBytes.Length, maxAvailableSpace - 1);
                        Array.Copy(encodedBytes, 0, fileBytes, (int)sub.Offset, safeLen);
                        fileBytes[(int)sub.Offset + safeLen] = 0x00;
                        modified = true;
                    }
                }
            }

            System.IO.File.WriteAllBytes(outputPath, fileBytes);
        }

        private int ReadInt32BigEndian(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(4);
            if (bytes.Length < 4) return 0;
            Array.Reverse(bytes);
            return BitConverter.ToInt32(bytes, 0);
        }
    }
}