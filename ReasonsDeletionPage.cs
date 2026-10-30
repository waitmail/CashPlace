namespace CashPlace;

/// <summary>
/// Модальный диалог выбора причины удаления (порт десктопного ReasonsDeletionCheck).
/// ВАРИАНТ Б: ОДИН файл, без XAML — весь интерфейс собирается на чистом C#.
///
/// Контракт с CheckPage.xaml.cs:
///     var page = new ReasonsDeletionPage(title, reasons);
///     await Navigation.PushModalAsync(page);
///     string? reason = await page.WaitAsync();   // null = «Отмена»/«Назад»
/// </summary>
public partial class ReasonsDeletionPage : ContentPage
{
    private readonly TaskCompletionSource<string?> _tcs = new();
    private bool _closed;

    public ReasonsDeletionPage(string title, string[] reasons)
    {
        // затемнённый полупрозрачный фон на весь экран, без панели навигации
        BackgroundColor = Color.FromArgb("#B3000000");
        NavigationPage.SetHasNavigationBar(this, false);

        var titleLabel = new Label
        {
            Text = title,
            FontSize = 17,
            FontAttributes = FontAttributes.Bold,
            HorizontalTextAlignment = TextAlignment.Center,
            TextColor = Color.FromArgb("#333333")
        };

        // кнопки-причины: перенос по словам — длинная причина видна ЦЕЛИКОМ, а не «…»
        var list = new VerticalStackLayout { Spacing = 10 };
        foreach (string reason in reasons)
        {
            var btn = new Button
            {
                Text = reason,
                FontSize = 15,
                BackgroundColor = Color.FromArgb("#2196F3"),
                TextColor = Colors.White,
                CornerRadius = 8,
                LineBreakMode = LineBreakMode.WordWrap,
                HorizontalOptions = LayoutOptions.Fill,
                Padding = new Thickness(12, 14)
            };
            btn.Clicked += (s, e) => Close(reason);
            list.Add(btn);
        }

        var cancelButton = new Button
        {
            Text = "ОТМЕНА",
            BackgroundColor = Color.FromArgb("#9E9E9E"),
            TextColor = Colors.White,
            FontSize = 15,
            FontAttributes = FontAttributes.Bold,
            CornerRadius = 8
        };
        cancelButton.Clicked += (s, e) => Close(null);

        // на маленьком экране список причин прокручивается
        var scroll = new ScrollView
        {
            Content = list,
            MaximumHeightRequest = 420
        };

        var card = new VerticalStackLayout { Spacing = 14, Padding = 16 };
        card.Add(titleLabel);
        card.Add(scroll);
        card.Add(cancelButton);

        Content = new Frame
        {
            Content = card,
            MaximumWidthRequest = 440,
            Margin = new Thickness(20),
            Padding = 0,
            CornerRadius = 14,
            BackgroundColor = Colors.White,
            HasShadow = true,
            IsClippedToBounds = true,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center
        };
    }

    private async void Close(string? result)
    {
        if (_closed) return;      // защита от двойного срабатывания
        _closed = true;

        await Navigation.PopModalAsync();
        _tcs.TrySetResult(result);
    }

    // системная кнопка «Назад» на Android = отмена
    protected override bool OnBackButtonPressed()
    {
        Close(null);
        return true;
    }

    public Task<string?> WaitAsync() => _tcs.Task;
}
