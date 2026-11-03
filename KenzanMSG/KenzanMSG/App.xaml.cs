using System.Text;
using System.Windows;

namespace KenzanMSG
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 💡 Registramos el proveedor de páginas de códigos antes de iniciar la app
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }
}
