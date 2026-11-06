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
                if (fs.Length < 0x200) return result;
                long length = fs.Length;

                // Escaneo robusto con filtros estrictos para evitar cabeceras y textos cortados
                for (long i = 0x18; i < length - 4; i++)
                {
                    fs.Seek(i, SeekOrigin.Begin);
                    ushort possibleStrLen = ReadUInt16BigEndian(reader);

                    // Longitud lógica esperada para un diálogo
                    if (possibleStrLen >= 2 && possibleStrLen < 1500)
                    {
                        if (i + 12 < length)
                        {
                            fs.Seek(i + 4, SeekOrigin.Begin);
                            int strOffset = ReadInt32BigEndian(reader);

                            // REGLA 1: El texto en los .msg de Yakuza siempre empieza pasado el offset 0x200 (fuera de la cabecera/cámaras)
                            // REGLA 2: Evitamos duplicados ya registrados
                            if (strOffset >= 0x200 && strOffset < length && !result.Any(s => s.Offset == strOffset))
                            {
                                // REGLA 3: Verificación estricta de que el offset apunta al inicio real de una cadena (el byte anterior debe ser 0x00)
                                if (IsAValidStringStart(fs, strOffset))
                                {
                                    string text = ReadNullTerminatedString(reader, strOffset, fs);

                                    // REGLA 4: Validar que el texto sea legítimo y tenga una longitud coherente
                                    if (!string.IsNullOrEmpty(text) && text.Length >= 2 && IsValidText(text))
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

        private bool IsAValidStringStart(FileStream fs, long offset)
        {
            try
            {
                // Si el offset es el inicio absoluto de la zona de strings o está precedido por un byte nulo (0x00), es un inicio válido
                if (offset <= 0x200) return false;
                fs.Seek(offset - 1, SeekOrigin.Begin);
                byte prevByte = (byte)fs.ReadByte();
                return prevByte == 0x00;
            }
            catch
            {
                return false;
            }
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
            // Descarta cadenas formadas por símbolos raros o basura pura
            int validChars = text.Count(c => char.IsLetterOrDigit(c) || char.IsPunctuation(c) || char.IsWhiteSpace(c));
            return (double)validChars / text.Length > 0.6;
        }

        public void RebuildWithSubtitles(string outputFolder, List<Subtitle> subtitles)
        {
            var outputPath = System.IO.Path.Combine(outputFolder, System.IO.Path.GetFileName(Path));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);

            if (subtitles == null || subtitles.Count == 0)
            {
                if (Path != outputPath)
                    System.IO.File.Copy(Path, outputPath, true);
                return;
            }

            // Leemos los bytes del archivo original de forma segura permitiendo lectura compartida
            byte[] fileBytes;
            using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fileBytes = new byte[fs.Length];
                fs.Read(fileBytes, 0, (int)fs.Length);
            }

            foreach (var sub in subtitles)
            {
                if (!string.IsNullOrEmpty(sub.Translation) && sub.Text != sub.Translation && sub.Offset >= 0x200 && sub.Offset < fileBytes.Length)
                {
                    byte[] oldBytes = FileEncoding.GetBytes(sub.Text);
                    byte[] newBytes = FileEncoding.GetBytes(sub.Translation);

                    bool match = true;
                    if (sub.Offset + oldBytes.Length <= fileBytes.Length)
                    {
                        for (int i = 0; i < oldBytes.Length; i++)
                        {
                            if (fileBytes[sub.Offset + i] != oldBytes[i])
                            {
                                match = false;
                                break;
                            }
                        }
                    }
                    else
                    {
                        match = false;
                    }

                    if (match)
                    {
                        int maxSpace = 0;
                        for (long i = sub.Offset; i < fileBytes.Length; i++)
                        {
                            if (fileBytes[i] == 0x00) break;
                            maxSpace++;
                        }
                        int totalAllowed = maxSpace + 1;

                        if (newBytes.Length <= totalAllowed)
                        {
                            Array.Clear(fileBytes, (int)sub.Offset, totalAllowed);
                            Array.Copy(newBytes, 0, fileBytes, (int)sub.Offset, newBytes.Length);
                            fileBytes[(int)sub.Offset + newBytes.Length] = 0x00;
                        }
                        else
                        {
                            int safeLen = totalAllowed - 1;
                            Array.Copy(newBytes, 0, fileBytes, (int)sub.Offset, safeLen);
                            fileBytes[(int)sub.Offset + safeLen] = 0x00;
                        }
                    }
                }
            }

            // 💡 Escribimos primero en un archivo temporal (.tmp) y luego lo reemplazamos de forma atómica para evitar bloqueos de Windows
            string tempPath = outputPath + ".tmp";
            System.IO.File.WriteAllBytes(tempPath, fileBytes);

            if (System.IO.File.Exists(outputPath))
            {
                System.IO.File.Delete(outputPath);
            }
            System.IO.File.Move(tempPath, outputPath);
        }

        private ushort ReadUInt16BigEndian(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(2);
            if (bytes.Length < 2) return 0;
            Array.Reverse(bytes);
            return BitConverter.ToUInt16(bytes, 0);
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