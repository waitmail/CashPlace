namespace CashPlace;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();

        //MainPage = new AppShell();
        MainPage = new NavigationPage(new AppShell()); // ★ Обернули в NavigationPage
    }
}