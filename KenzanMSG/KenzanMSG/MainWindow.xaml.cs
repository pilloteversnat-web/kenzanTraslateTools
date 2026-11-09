using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace KenzanMSG
{
    public partial class MainWindow : Window
    {
        private string _selectedFolderPath = string.Empty;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void BtnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Selecciona la carpeta de archivos MSG/TXT"
            };

            if (dialog.ShowDialog() == true)
            {
                _selectedFolderPath = dialog.FolderName;
                txtFolderPath.Text = _selectedFolderPath;
                Log($"Carpeta seleccionada: {_selectedFolderPath}");
            }
        }

        private async void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedFolderPath) || !Directory.Exists(_selectedFolderPath))
            {
                MessageBox.Show("Por favor, selecciona una carpeta válida primero.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ToggleControls(false);
            lblStatus.Content = "Exportando y filtrando ASCII...";
            txtLog.Clear();

            await Task.Run(() =>
            {
                try
                {
                    var msgFiles = Directory.GetFiles(_selectedFolderPath, "*.msg", SearchOption.AllDirectories);
                    Log($"Se encontraron {msgFiles.Length} archivos .msg para exportar.");

                    foreach (var file in msgFiles)
                    {
                        try
                        {
                            var msgFileObj = new KenzanMSG.File("Kenzan", file, string.Empty, Encoding.GetEncoding(932));

                            var txtOutputPath = Path.ChangeExtension(file, ".txt");
                            using var writer = new StreamWriter(txtOutputPath, false, Encoding.UTF8);

                            var subtitles = msgFileObj.GetSubtitles();

                            if (subtitles != null)
                            {
                                int index = 0;
                                foreach (var sub in subtitles)
                                {
                                    if (!string.IsNullOrEmpty(sub.Text))
                                    {
                                        string asciiOnly = CleanToAscii(sub.Text);
                                        writer.WriteLine($"# Offset: {sub.Offset}");
                                        writer.WriteLine(asciiOnly);
                                        writer.WriteLine("---");
                                        index++;
                                    }
                                }
                                Log($"[EXPORTADO] {Path.GetFileName(file)} -> {index} líneas extraídas.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"[ERROR] Falló al exportar {Path.GetFileName(file)}: {ex.Message}");
                        }
                    }
                    Log("¡Proceso de exportación finalizado!");
                }
                catch (Exception ex)
                {
                    Log($"[CRÍTICO] Error general: {ex.Message}");
                }
            });

            ToggleControls(true);
            lblStatus.Content = "Exportación completada.";
        }

        private async void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedFolderPath) || !Directory.Exists(_selectedFolderPath))
            {
                MessageBox.Show("Por favor, selecciona una carpeta válida primero.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ToggleControls(false);
            lblStatus.Content = "Importando traducciones a MSG...";
            txtLog.Clear();

            await Task.Run(() =>
            {
                try
                {
                    var txtFiles = Directory.GetFiles(_selectedFolderPath, "*.txt", SearchOption.AllDirectories);
                    Log($"Se encontraron {txtFiles.Length} archivos .txt para importar.");

                    foreach (var txtFile in txtFiles)
                    {
                        try
                        {
                            var correspondingMsg = Path.ChangeExtension(txtFile, ".msg");
                            if (!System.IO.File.Exists(correspondingMsg)) continue;

                            var msgFileObj = new KenzanMSG.File("Kenzan", correspondingMsg, _selectedFolderPath, Encoding.GetEncoding(932));
                            var subtitles = msgFileObj.GetSubtitles();

                            if (subtitles != null && subtitles.Count > 0)
                            {
                                // Lectura secuencial por bloques del archivo TXT
                                var fileLines = System.IO.File.ReadAllLines(txtFile, Encoding.UTF8);
                                var translationsList = new List<string>();
                                string currentTrans = "";
                                bool capturing = false;

                                foreach (var line in fileLines)
                                {
                                    if (line.StartsWith("# Offset:"))
                                    {
                                        if (capturing)
                                        {
                                            translationsList.Add(currentTrans.TrimEnd('\r', '\n'));
                                            currentTrans = "";
                                        }
                                        capturing = true;
                                    }
                                    else if (line == "---")
                                    {
                                        if (capturing)
                                        {
                                            translationsList.Add(currentTrans.TrimEnd('\r', '\n'));
                                            currentTrans = "";
                                            capturing = false;
                                        }
                                    }
                                    else if (capturing)
                                    {
                                        currentTrans += (string.IsNullOrEmpty(currentTrans) ? "" : "\n") + line;
                                    }
                                }

                                // Asignación directa 1 a 1 por índice ordenado
                                int limit = Math.Min(subtitles.Count, translationsList.Count);
                                for (int i = 0; i < limit; i++)
                                {
                                    subtitles[i].Translation = translationsList[i].Replace("\\n", "\n");
                                }

                                // Ejecutamos la reconstrucción limpia del MSG
                                msgFileObj.RebuildWithSubtitles(_selectedFolderPath, subtitles);
                                Log($"[IMPORTADO] {Path.GetFileName(correspondingMsg)} -> {limit} líneas aplicadas.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"[ERROR] Falló al importar {Path.GetFileName(txtFile)}: {ex.Message}");
                        }
                    }
                    Log("¡Proceso de importación y reconstrucción finalizado con éxito!");
                }
                catch (Exception ex)
                {
                    Log($"[CRÍTICO] Error general en importación: {ex.Message}");
                }
            });

            ToggleControls(true);
            lblStatus.Content = "Importación completada.";
        }

        private string CleanToAscii(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return Regex.Replace(input, @"[^\u0020-\u007E\r\n]", string.Empty);
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
                txtLog.ScrollToEnd();
            });
        }

        private void ToggleControls(bool state)
        {
            Dispatcher.Invoke(() =>
            {
                btnExport.IsEnabled = state;
                btnImport.IsEnabled = state;
                btnSelectFolder.IsEnabled = state;
            });
        }
    }
}