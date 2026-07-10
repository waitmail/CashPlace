using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.IO;


namespace CashPlace;

public partial class MainPage : ContentPage
{
    private string _dbPath => Path.Combine(FileSystem.AppDataDirectory, "cashplace.db");

    public MainPage()
    {
        InitializeComponent();
    }

    // ★ ВЫЗЫВАЕМ ПРОВЕРКУ ПОСЛЕ ОТРИСОВКИ СТРАНИЦЫ ★
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await CheckDatabaseStateAsync();
        
        // ★ Автоматическая проверка при старте (только если настройки введены) ★
        if (WorkLayout.IsVisible)
        {
            await PerformUpdateCheck(false);
        }
    }
    
    // ★ Метод проверки обновлений ★
    // ★ Метод проверки обновлений ★
    private async Task PerformUpdateCheck(bool showMessageBoxIfNoUpdate)
    {
        try
        {
            CheckUpdateBtn.Text = "Проверка...";
            CheckUpdateBtn.IsEnabled = false;

            // Теперь метод возвращает bool? (true, false или null)
            bool? updateStatus = await AppUpdater.CheckAndDownloadUpdateAsync();

            if (updateStatus == true)
            {
                // Обновление скачалось, показываем кнопку "Установить"
                UpdateBtn.IsVisible = true;
            }
            else if (updateStatus == false)
            {
                // Сервер ответил, что обновлений нет
                UpdateBtn.IsVisible = false;
                if (showMessageBoxIfNoUpdate)
                {
                    await DisplayAlert("Обновления", "У вас установлена последняя версия программы.", "OK");
                }
            }
            else // updateStatus == null
            {
                // Произошла ошибка связи с сервером
                UpdateBtn.IsVisible = false;
                if (showMessageBoxIfNoUpdate)
                {
                    await DisplayAlert("Ошибка", "Не удалось связаться с сервером обновлений. Проверьте интернет-соединение или настройки.", "OK");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Update check failed: {ex.Message}");
        }
        finally
        {
            CheckUpdateBtn.Text = "Проверить обновления";
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    // ★ Обработчик кнопки "Проверить обновления" ★
    private async void OnCheckUpdateClicked(object sender, EventArgs e)
    {
        await PerformUpdateCheck(true);
    }

    // ★ Обработчик кнопки "Установить обновление" ★
    private void OnInstallUpdateClicked(object sender, EventArgs e)
    {
        AppUpdater.InstallDownloadedUpdate();
    }

    private async Task CheckDatabaseStateAsync()
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS constants (code_shop TEXT, nick_shop TEXT, path_for_web_service TEXT, last_date_download_bonus_clients TEXT)";
            await command.ExecuteNonQueryAsync();

            // Читаем ник магазина
            command.CommandText = "SELECT nick_shop FROM constants LIMIT 1";
            using var reader = await command.ExecuteReaderAsync();

            if (await reader.ReadAsync() && !reader.IsDBNull(0) && !string.IsNullOrWhiteSpace(reader.GetString(0)))
            {
                // Запись есть - показываем рабочий экран
                StoreNickLabel.Text = reader.GetString(0);
                
                SetupLayout.IsVisible = false;
                WorkLayout.IsVisible = true;
                VersionLabel.Text = $"Версия: {MainStaticClass.GetProductVersion()}";
            }
            else
            {
                // Записи нет - показываем экран настройки
                SetupLayout.IsVisible = true;
                WorkLayout.IsVisible = false;
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка БД", ex.Message, "OK");
        }
    }
    
    private async void OnScanClicked(object sender, EventArgs e)
    {
        // Открываем страницу сканера
        var scannerPage = new ScannerPage(async (result) =>
        {
            // Когда код отсканирован, вставляем его в текстовое поле
            QrCodeEntry.Text = result;
        });

        await Navigation.PushModalAsync(scannerPage);
    }

    private async void OnSaveSettingsClicked(object sender, EventArgs e)
    {
        string encryptedText = QrCodeEntry.Text?.Trim();

        if (string.IsNullOrEmpty(encryptedText))
        {
            await DisplayAlert("Ошибка", "Поле ввода пустое", "OK");
            return;
        }

        try
        {
            // 1. Расшифровываем
            string decryptedJson = AesQrCrypto.Decrypt(encryptedText);

            // 2. Парсим JSON
            var config = JsonSerializer.Deserialize<StoreConfig>(decryptedJson);

            // 3. Проверяем срок действия
            long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (config.Exp < currentTimestamp)
            {
                await DisplayAlert("Ошибка", "Срок действия QR-кода истек.", "OK");
                return;
            }

            // 4. Сохраняем в БД
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            // Формируем JSON с URL-адресами
            string[] defaultUrls = new[] {
                "http://ch1.sd2.com.ru/DiscountSystem/ds.asmx",
                "http://ch2.sd2.com.ru/DiscountSystem/ds.asmx",
                "http://ch3.sd2.com.ru/DiscountSystem/ds.asmx"
            };
            string jsonUrls = JsonSerializer.Serialize(defaultUrls);

            var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO constants (code_shop, nick_shop, path_for_web_service) VALUES (@code, @nick, @path)";
            command.Parameters.AddWithValue("@code", config.Guid);
            command.Parameters.AddWithValue("@nick", config.Nick);
            command.Parameters.AddWithValue("@path", jsonUrls);

            await command.ExecuteNonQueryAsync();

            // 5. Переключаем экран
            await CheckDatabaseStateAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", "Неверный или поврежденный QR-код", "OK");
        }
    }
}