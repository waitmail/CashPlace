namespace CashPlace;

public partial class AppShell : TabbedPage
{
    public AppShell()
    {
        InitializeComponent();
        this.CurrentPageChanged += OnCurrentPageChanged;
    }

    private async void OnCurrentPageChanged(object sender, EventArgs e)
    {
        // Если пользователь попытался уйти не на главную страницу
        if (this.CurrentPage is not MainPage)
        {
            // Проверяем, заполнены ли настройки
            if (string.IsNullOrWhiteSpace(MainStaticClass.Code_Shop) || 
                string.IsNullOrWhiteSpace(MainStaticClass.Nick_Shop))
            {
                // Возвращаем пользователя на главную страницу (индекс 0)
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    this.CurrentPage = this.Children[0];
                    
                    // Показываем предупреждение
                    await DisplayAlert("Доступ закрыт", "Сначала отсканируйте QR-код на главной странице для настройки приложения.", "OK");
                });
            }
        }
    }
}