namespace CashPlace;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
    }

    protected override async void OnNavigating(ShellNavigatingEventArgs args)
    {
        base.OnNavigating(args);

        // Получаем адрес (URL) страницы, на которую хотим перейти
        string targetRoute = args.Target.Location.OriginalString;

        // Проверяем, пытается ли пользователь уйти на "Загрузку" или "Чеки"
        if (targetRoute.Contains(nameof(LoadDataPage)) || targetRoute.Contains(nameof(ChecksPage)))
        {
            // Проверяем, заполнены ли настройки
            if (string.IsNullOrWhiteSpace(MainStaticClass.Code_Shop) || 
                string.IsNullOrWhiteSpace(MainStaticClass.Nick_Shop))
            {
                // Отменяем переход ВЫЗовом МЕТОДА!
                args.Cancel(); 

                // Показываем предупреждение
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await DisplayAlert("Доступ закрыт", "Сначала отсканируйте QR-код на главной странице для настройки приложения.", "OK");
                });
            }
        }
    }
}