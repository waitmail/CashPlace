namespace CashPlace;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();

// #if WINDOWS
//         // ★ окно отладки под Windows — портретные пропорции, как на планшете.
//         //   Чисто косметика: на Android этот блок не компилируется вообще.
//         Microsoft.Maui.Handlers.WindowHandler.Mapper.AppendToMapping("PortraitWindow", (handler, view) =>
//         {
//             var appWindow = handler.PlatformView.AppWindow;
//             appWindow.Resize(new Windows.Graphics.SizeInt32(600, 1027));   // ← подгони под свой монитор
//         });
// #endif

        //MainPage = new AppShell();
        MainPage = new NavigationPage(new AppShell()); // ★ Обернули в NavigationPage
    }
}