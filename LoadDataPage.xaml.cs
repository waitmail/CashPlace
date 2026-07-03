using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Globalization;

namespace CashPlace;

public partial class LoadDataPage : ContentPage
{
    // Элементы управления (теперь они доступны напрямую по x:Name)
    private bool _isLoading = false;
    private CancellationTokenSource _cancellationTokenSource;
    private readonly TimeSpan _loadTimeout = TimeSpan.FromMinutes(30);
    private Timer _timer;
    private Stopwatch _stopwatch;
    private bool _userCancelled = false;

    // Путь к базе данных SQLite на Android
    private string DbPath => Path.Combine(FileSystem.AppDataDirectory, "cashplace.db");

    private const bool USE_OPTIMIZED_SYNC = true;

    #region Классы данных

    public class LoadPacketData : IDisposable
    {
        public int Threshold { get; set; }
        public List<Tovar> ListTovar { get; set; }
        public List<Barcode> ListBarcode { get; set; }
        public List<ActionHeader> ListActionHeader { get; set; }
        public List<ActionTable> ListActionTable { get; set; }
        public List<Characteristic> ListCharacteristic { get; set; }
        public List<Sertificate> ListSertificate { get; set; }
        public List<PromoText> ListPromoText { get; set; }
        public List<ActionClients> ListActionClients { get; set; }
        public bool PacketIsFull { get; set; }
        public bool Exchange { get; set; }
        public string Exception { get; set; }
        public string TokenMark { get; set; }

        public void Dispose()
        {
            ListTovar?.Clear(); ListBarcode?.Clear(); ListActionHeader?.Clear();
            ListActionTable?.Clear(); ListCharacteristic?.Clear(); ListSertificate?.Clear();
            ListPromoText?.Clear(); ListActionClients?.Clear();
            ListTovar = null; ListBarcode = null; ListActionHeader = null; ListActionTable = null;
            ListCharacteristic = null; ListSertificate = null; ListPromoText = null; ListActionClients = null;
        }
    }

    public class Tovar { public string Code { get; set; } public string Name { get; set; } public string RetailPrice { get; set; } public string ItsDeleted { get; set; } public string Nds { get; set; } public string ItsCertificate { get; set; } public string PercentBonus { get; set; } public string TnVed { get; set; } public string ItsMarked { get; set; } public string ItsExcise { get; set; } public string CdnCheck { get; set; } public string Fractional { get; set; } public string RefusalOfMarking { get; set; } public string RrNotControlOwner { get; set; } }
    public class Barcode { public string BarCode { get; set; } public string TovarCode { get; set; } }
    public class ActionHeader { public string DateStarted { get; set; } public string DateEnd { get; set; } public string NumDoc { get; set; } public string Tip { get; set; } public string Barcode { get; set; } public string Persent { get; set; } public string sum { get; set; } public string sum1 { get; set; } public string Comment { get; set; } public string Marker { get; set; } public string ActionByDiscount { get; set; } public string TimeStart { get; set; } public string TimeEnd { get; set; } public string BonusPromotion { get; set; } public string WithOldPromotion { get; set; } public string Monday { get; set; } public string Tuesday { get; set; } public string Wednesday { get; set; } public string Thursday { get; set; } public string Friday { get; set; } public string Saturday { get; set; } public string Sunday { get; set; } public string PromoCode { get; set; } public string SumBonus { get; set; } public string ExecutionOrder { get; set; } public string GiftPrice { get; set; } public string Kind { get; set; } public string Picture { get; set; } }
    public class ActionTable { public string NumDoc { get; set; } public string NumList { get; set; } public string CodeTovar { get; set; } public string Price { get; set; } }
    public class Characteristic { public string CodeTovar { get; set; } public string Name { get; set; } public string Guid { get; set; } public string RetailPrice { get; set; } }
    public class Sertificate { public string Code { get; set; } public string CodeTovar { get; set; } public string Rating { get; set; } public string IsActive { get; set; } }
    public class PromoText { public string AdvertisementText { get; set; } public string NumStr { get; set; } public string Picture { get; set; } }
    public class ActionClients { public string NumDoc { get; set; } public string CodeClient { get; set; } }
    public class Client { public string code { get; set; } public string phone { get; set; } public string name { get; set; } public string holiday { get; set; } public string use_blocked { get; set; } public string reason_for_blocking { get; set; } public string notify_security { get; set; } public string datetime_update { get; set; } }
    public class Clients { public List<Client> list_clients { get; set; } }
    public class QueryPacketData : IDisposable { public string Version { get; set; } public string NickShop { get; set; } public string CodeShop { get; set; } public string LastDateDownloadTovar { get; set; } public string NumCash { get; set; } public void Dispose() { } }

    #endregion

    public LoadDataPage()
    {
        InitializeComponent();
        InitializeDatabase();
        //InitializeConstants();
        UpdateLastSyncDate();
    }

    private void InitializeDatabase()
{
    using var conn = new SqliteConnection($"Data Source={DbPath}");
    conn.Open();
    
    // ОТКЛЮЧАЕМ WAL (чтобы база была в одном файле и легко читалась на ПК)
    using var pragmaCmd = new SqliteCommand("PRAGMA journal_mode=DELETE;", conn);
    pragmaCmd.ExecuteNonQuery();

    string sql = @"
        CREATE TABLE IF NOT EXISTS tovar (code INTEGER PRIMARY KEY, name TEXT, retail_price REAL, its_deleted INTEGER, nds INTEGER, its_certificate INTEGER, percent_bonus REAL, tnved TEXT, its_marked INTEGER, its_excise INTEGER, cdn_check INTEGER, fractional INTEGER, refusal_of_marking INTEGER, rr_not_control_owner INTEGER);
        CREATE TABLE IF NOT EXISTS tovar2 (code INTEGER PRIMARY KEY, name TEXT, retail_price REAL, its_deleted INTEGER, nds INTEGER, its_certificate INTEGER, percent_bonus REAL, tnved TEXT, its_marked INTEGER, its_excise INTEGER, cdn_check INTEGER, fractional INTEGER, refusal_of_marking INTEGER, rr_not_control_owner INTEGER);
        CREATE TABLE IF NOT EXISTS barcode (tovar_code INTEGER, barcode TEXT);
        CREATE TABLE IF NOT EXISTS sertificates (code INTEGER, code_tovar INTEGER, rating REAL, is_active INTEGER);
        CREATE TABLE IF NOT EXISTS action_header (date_started TEXT, date_end TEXT, num_doc INTEGER, tip INTEGER, barcode TEXT, persent REAL, sum REAL, comment TEXT, marker INTEGER, action_by_discount INTEGER, time_start TEXT, time_end TEXT, bonus_promotion INTEGER, with_old_promotion INTEGER, monday INTEGER, tuesday INTEGER, wednesday INTEGER, thursday INTEGER, friday INTEGER, saturday INTEGER, sunday INTEGER, promo_code TEXT, sum_bonus REAL, execution_order INTEGER, gift_price REAL, kind INTEGER, sum1 REAL, picture TEXT);
        CREATE TABLE IF NOT EXISTS action_table (num_doc INTEGER, num_list INTEGER, code_tovar INTEGER, price REAL);
        CREATE TABLE IF NOT EXISTS advertisement (advertisement_text TEXT, num_str INTEGER, picture TEXT);
        CREATE TABLE IF NOT EXISTS action_clients (num_doc INTEGER, code_client TEXT);
        CREATE TABLE IF NOT EXISTS clients (code TEXT PRIMARY KEY, phone TEXT, name TEXT, date_of_birth TEXT, its_work INTEGER, reason_for_blocking TEXT, notify_security INTEGER, last_server_sync TEXT);
        CREATE TABLE IF NOT EXISTS date_sync (id INTEGER PRIMARY KEY, tovar TEXT, client TEXT);
        CREATE TABLE IF NOT EXISTS constants (code_shop TEXT, nick_shop TEXT, path_for_web_service TEXT, last_date_download_bonus_clients TEXT);

            CREATE TABLE IF NOT EXISTS checks_header (
            document_number INTEGER PRIMARY KEY, 
            date_time_start TEXT, 
            date_time_write TEXT, 
            client TEXT, 
            cash_desk_number INTEGER, 
            comment TEXT, 
            cash REAL, 
            remainder REAL, 
            discount REAL, 
            autor TEXT, 
            its_deleted INTEGER, 
            check_type INTEGER, 
            its_print INTEGER, 
            its_print_p INTEGER, 
            extra INTEGER,
            guid TEXT,           
            is_sent INTEGER DEFAULT 0  
        );

        CREATE TABLE IF NOT EXISTS checks_table (
            document_number INTEGER, 
            tovar_code INTEGER, 
            name TEXT, 
            quantity REAL, 
            price REAL, 
            price_at_a_discount REAL, 
            sum REAL, 
            sum_at_a_discount REAL,
            guid TEXT,           
            item_marker TEXT     
        );
    ";
    using var cmd = new SqliteCommand(sql, conn);
    cmd.ExecuteNonQuery();
}

    // private void InitializeConstants()
    // {
    //     try
    //     {
    //         using var conn = new SqliteConnection($"Data Source={DbPath}");
    //         conn.Open();
    //
    //         // 1. Проверяем, есть ли уже записи в таблице constants
    //         string checkQuery = "SELECT COUNT(*) FROM constants;";
    //         using var checkCmd = new SqliteCommand(checkQuery, conn);
    //         long rowCount = (long)checkCmd.ExecuteScalar();
    //
    //         // 2. Если записей нет, делаем вставку
    //         if (rowCount == 0)
    //         {
    //             // Задаем начальные значения
    //             string defaultCodeShop = MainStaticClass.Code_Shop; //"181cdd69-5b7b-11e4-80d6-00e081e3d824";
    //             string defaultNickShop = MainStaticClass.Nick_Shop; //"A05";
    //
    //             // Ваша строка с адресами через запятую
    //             string rawUrls =
    //                 "http://ch1.sd2.com.ru/DiscountSystem/ds.asmx,http://ch2.sd2.com.ru/DiscountSystem/ds.asmx,http://ch3.sd2.com.ru/DiscountSystem/ds.asmx";
    //             //string rawUrls = "http://localhost:50520/DS.asmx";
    //
    //             // Разбиваем строку по запятой в массив string[]
    //             // StringSplitOptions.RemoveEmptyEntries уберет пустые элементы, если вдруг две запятые подряд
    //             string[] defaultUrls = rawUrls.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
    //
    //             // Превращаем массив в JSON-строку: 
    //             // ["http://ch1.sd2.com.ru/DiscountSystem/ds.asmx","http://ch2.sd2.com.ru/DiscountSystem/ds.asmx","http://ch3.sd2.com.ru/DiscountSystem/ds.asmx"]
    //             string jsonUrls = JsonConvert.SerializeObject(defaultUrls);
    //
    //             string insertQuery =
    //                 "INSERT INTO constants (code_shop, nick_shop, path_for_web_service) VALUES (@code_shop, @nick_shop, @path);";
    //             using var insertCmd = new SqliteCommand(insertQuery, conn);
    //
    //             insertCmd.Parameters.AddWithValue("@code_shop", defaultCodeShop);
    //             insertCmd.Parameters.AddWithValue("@nick_shop", defaultNickShop);
    //             insertCmd.Parameters.AddWithValue("@path", jsonUrls); // Вставляем JSON
    //
    //             insertCmd.ExecuteNonQuery();
    //             Debug.WriteLine("[DB] В таблицу constants добавлены значения по умолчанию.");
    //         }
    //     }
    //     catch (Exception ex)
    //     {
    //         Debug.WriteLine($"[DB] Ошибка при инициализации constants: {ex.Message}");
    //     }
    // }

    private async void BtnLoad_Clicked(object sender, EventArgs e)
    {
        await StartAsyncLoad();
    }

    #region Основная логика загрузки

    private async Task StartAsyncLoad()
    {
        if (_isLoading)
        {
            await DisplayAlert("Информация", "Загрузка уже выполняется", "OK");
            return;
        }

        bool confirm = await DisplayAlert("Подтверждение", "Выполнить загрузку данных из системы?", "Да", "Нет");
        if (!confirm) return;

        _userCancelled = false;
        _cancellationTokenSource = new CancellationTokenSource();
        _stopwatch = Stopwatch.StartNew();

        try
        {
            await SetLoadingStateAsync(true);
            StartTimer();

            var loadTask = Task.Run(async () =>
            {
                try { return await PerformFullLoadAsync(_cancellationTokenSource.Token); }
                catch (OperationCanceledException) { return (false, "Операция отменена пользователем"); }
                catch (Exception ex) { return (false, $"Ошибка при выполнении загрузки: {ex.Message}"); }
            }, _cancellationTokenSource.Token);

            var timeoutTask = Task.Delay(_loadTimeout, _cancellationTokenSource.Token);
            var completedTask = await Task.WhenAny(loadTask, timeoutTask);

            if (completedTask == timeoutTask && !_userCancelled)
            {
                await HandleTimeoutAsync();
                return;
            }

            var (success, errorMessage) = await loadTask;
            if (!_userCancelled) await HandleLoadResultAsync(success, errorMessage);
        }
        catch (Exception ex)
        {
            if (!_userCancelled) await DisplayAlert("Ошибка", $"Ошибка при запуске: {ex.Message}", "OK");
        }
        finally
        {
            await SetLoadingStateAsync(false);
            StopTimer();
            _stopwatch?.Stop();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private async Task SetLoadingStateAsync(bool isLoading)
    {
        _isLoading = isLoading;
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            BtnLoad.Text = isLoading ? "Идет загрузка..." : "Начать загрузку данных";
            BtnLoad.BackgroundColor = isLoading ? Color.FromArgb("#4CAF50") : Color.FromArgb("#2196F3");
            BtnLoad.IsEnabled = !isLoading;
            ProgressPanel.IsVisible = isLoading;
            WorkHintText.IsVisible = isLoading;
            TimeInfoText.IsVisible = isLoading;
            if (isLoading) TimeInfoText.Text = "Время загрузки: 00:00";
            else { ProgressBar1.Progress = 0; StatusText.Text = ""; ProgressPercent.Text = "0%"; }
        });
    }

    private async Task<(bool success, string errorMessage)> PerformFullLoadAsync(CancellationToken ct)
    {
        try
        {
            await UpdateProgressAsync("Проверка соединения с веб-сервисом...", 5);
            if (!await CheckServiceAvailabilityAsync(ct)) return (false, "Веб-сервис недоступен");

            await UpdateProgressAsync("Получение данных с сервера...", 15);
            var serverData = await GetDataFromServerAsync(ct);
            if (!serverData.success) return (false, serverData.errorMessage);

            await UpdateProgressAsync("Подготовка к сохранению данных...", 20);
            var saveResult = await SaveDataToDatabaseAsync(serverData.data, ct, 20, 80);
            if (!saveResult.success) return (false, saveResult.errorMessage);

            await UpdateProgressAsync("Завершение операций с базой данных...", 85);
            await FinalizeLoadAsync(ct);

            await UpdateProgressAsync("Готово", 100);
            UpdateLastSyncDate();
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка: {ex.Message}");
        }
    }

    #region Таймер
    private void StartTimer()
    {
        _timer = new Timer(_ =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_stopwatch != null && _stopwatch.IsRunning)
                    TimeInfoText.Text = $"Время загрузки: {_stopwatch.Elapsed:mm\\:ss}";
            });
        }, null, 0, 1000);
    }

    private void StopTimer()
    {
        _timer?.Dispose(); _timer = null;
        if (_stopwatch != null)
        {
            _stopwatch.Stop();
            MainThread.BeginInvokeOnMainThread(() => TimeInfoText.Text = $"Общее время: {_stopwatch.Elapsed:mm\\:ss}");
        }
    }
    #endregion

    #region Методы загрузки и БД (Адаптировано для SQLite)

    private async Task<bool> CheckServiceAvailabilityAsync(CancellationToken ct) => await Task.Run(() => MainStaticClass.ServiceIsWorkerAsync(), ct);

    private async Task<(bool success, LoadPacketData data, string errorMessage)> GetDataFromServerAsync(CancellationToken ct)
    {
        try
        {
            string nick_shop = MainStaticClass.Nick_Shop?.Trim();
            string code_shop = MainStaticClass.Code_Shop?.Trim();
            if (string.IsNullOrEmpty(nick_shop) || string.IsNullOrEmpty(code_shop)) return (false, null, "Нет настроек магазина");

            string key = nick_shop + CryptorEngine.GetCountDay() + code_shop;
            using var queryPacketData = new QueryPacketData();
            queryPacketData.NickShop = nick_shop;
            queryPacketData.CodeShop = code_shop;
            queryPacketData.LastDateDownloadTovar = last_date_download_tovars().ToString("dd-MM-yyyy");
            queryPacketData.NumCash = "11";//MainStaticClass.CashDeskNumber.ToString();
            queryPacketData.Version = MainStaticClass.GetProductVersion().Replace(".", "");

            string data = JsonConvert.SerializeObject(queryPacketData, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            string data_encrypt = CryptorEngine.Encrypt(data, true, key);

            var loadPacketData = await getLoadPacketDataFullAsync(nick_shop, data_encrypt, key);
            if (loadPacketData == null) return (false, null, "null результат");
            if (!loadPacketData.PacketIsFull) return (false, null, "Пакет не полный: " + loadPacketData.Exception);
            if (loadPacketData.Exchange) return (false, null, "Обновление данных на сервере");

            return (true, loadPacketData, "");
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    private async Task<LoadPacketData> getLoadPacketDataFullAsync(string nick_shop, string data_encrypt, string key)
    {
        var loadPacketData = new LoadPacketData { PacketIsFull = false };
        try
        {
            DS ds = await ServiceLocator.DsAsync(); ds.Timeout = 60000;
            //byte[] result_query_byte = await ds.GetDataForCasheV8JsonAvalonAsync(nick_shop, data_encrypt, MainStaticClass.GetWorkSchema.ToString());
            byte[] result_query_byte = await ds.GetDataForCasheV8JsonAvalonAsync(nick_shop, data_encrypt, "10");
            string result_query = DecompressString(result_query_byte);
            string decrypt_data = CryptorEngine.Decrypt(result_query, true, key);
            loadPacketData = JsonConvert.DeserializeObject<LoadPacketData>(decrypt_data);
        }
        catch (Exception ex) { await ServiceLocator.ResetDsCacheAsync(); loadPacketData.Exception = ex.Message; }
        return loadPacketData;
    }

    private string DecompressString(byte[] value)
    {
        if (value == null || value.Length == 0) return "";
        using MemoryStream stream = new MemoryStream(value);
        using System.IO.Compression.GZipStream zip = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
        using StreamReader reader = new StreamReader(zip);
        return reader.ReadToEnd();
    }

    private async Task<(bool success, string errorMessage)> SaveDataToDatabaseAsync(LoadPacketData loadData, CancellationToken ct, int startProgress, int endProgress)
    {
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        await conn.OpenAsync(ct);
        using var tran = await conn.BeginTransactionAsync(ct) as SqliteTransaction;

        try
        {
            // Очистка таблиц
            await ExecNonQueryAsync("DELETE FROM action_table; DELETE FROM action_header; DELETE FROM advertisement; DELETE FROM action_clients;", conn, tran, ct);

            // if (!string.IsNullOrEmpty(loadData.TokenMark))
            //     await ExecNonQueryAsync($"UPDATE constants SET cdn_token='{EscapeSql(loadData.TokenMark)}'", conn, tran, ct);

            await UpdateProgressAsync("Удаление старых товаров...", startProgress);
            await ExecNonQueryAsync("DELETE FROM tovar;", conn, tran, ct);

            if (loadData.ListTovar?.Count > 0)
            {
                await UpdateProgressAsync("Вставка товаров...", startProgress + 10);

                // 1. Создаем команду ОДИН раз до цикла
                string sql =
                    "INSERT INTO tovar (code, name, retail_price, its_deleted, nds, its_certificate, percent_bonus, tnved, its_marked, its_excise, cdn_check, fractional, refusal_of_marking, rr_not_control_owner) " +
                    "VALUES (@code, @name, @price, @del, @nds, @cert, @pb, @tnved, @marked, @excise, @cdn, @frac, @refusal, @rr)";
                using var cmd = new SqliteCommand(sql, conn, tran);

                // 2. Добавляем параметры ОДИН раз (указываем типы для скорости)
                cmd.Parameters.Add("@code", SqliteType.Integer);
                cmd.Parameters.Add("@name", SqliteType.Text);
                cmd.Parameters.Add("@price", SqliteType.Real);
                cmd.Parameters.Add("@del", SqliteType.Integer);
                cmd.Parameters.Add("@nds", SqliteType.Integer);
                cmd.Parameters.Add("@cert", SqliteType.Integer);
                cmd.Parameters.Add("@pb", SqliteType.Real);
                cmd.Parameters.Add("@tnved", SqliteType.Text);
                cmd.Parameters.Add("@marked", SqliteType.Integer);
                cmd.Parameters.Add("@excise", SqliteType.Integer);
                cmd.Parameters.Add("@cdn", SqliteType.Integer);
                cmd.Parameters.Add("@frac", SqliteType.Integer);
                cmd.Parameters.Add("@refusal", SqliteType.Integer);
                cmd.Parameters.Add("@rr", SqliteType.Integer);

                // 3. В цикле только меняем значения и выполняем
                foreach (var t in loadData.ListTovar)
                {
                    if (ct.IsCancellationRequested) break;

                    cmd.Parameters["@code"].Value = long.TryParse(t.Code, out long c) ? c : 0;
                    cmd.Parameters["@name"].Value = t.Name ?? "";
                    cmd.Parameters["@price"].Value = decimal.TryParse(t.RetailPrice, NumberStyles.Any,
                        CultureInfo.InvariantCulture, out decimal p)
                        ? p
                        : 0;
                    cmd.Parameters["@del"].Value = int.TryParse(t.ItsDeleted, out int d) ? d : 0;
                    cmd.Parameters["@nds"].Value = int.TryParse(t.Nds, out int n) ? n : 0;
                    cmd.Parameters["@cert"].Value = int.TryParse(t.ItsCertificate, out int cert) ? cert : 0;
                    cmd.Parameters["@pb"].Value = decimal.TryParse(t.PercentBonus, out decimal pb) ? pb : 0;
                    cmd.Parameters["@tnved"].Value = t.TnVed ?? "";
                    cmd.Parameters["@marked"].Value = int.TryParse(t.ItsMarked, out int m) ? m : 0;
                    cmd.Parameters["@excise"].Value = int.TryParse(t.ItsExcise, out int e) ? e : 0;
                    cmd.Parameters["@cdn"].Value = t.CdnCheck == "1" ? 1 : 0;
                    cmd.Parameters["@frac"].Value = t.Fractional == "1" ? 1 : 0;
                    cmd.Parameters["@refusal"].Value = t.RefusalOfMarking == "1" ? 1 : 0;
                    cmd.Parameters["@rr"].Value = t.RrNotControlOwner == "1" ? 1 : 0;

                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }

            if (loadData.ListBarcode?.Count > 0)
            {
                await ExecNonQueryAsync("DELETE FROM barcode;", conn, tran, ct);
                foreach (var b in loadData.ListBarcode)
                {
                    string sql = "INSERT INTO barcode (tovar_code, barcode) VALUES (@code, @bar)";
                    using var cmd = new SqliteCommand(sql, conn, tran);
                    cmd.Parameters.AddWithValue("@code", long.TryParse(b.TovarCode, out long c) ? c : 0);
                    cmd.Parameters.AddWithValue("@bar", b.BarCode ?? "");
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }

            if (loadData.ListSertificate?.Count > 0)
            {
                await ExecNonQueryAsync("DELETE FROM sertificates;", conn, tran, ct);
                foreach (var s in loadData.ListSertificate)
                {
                    string sql = "INSERT INTO sertificates (code, code_tovar, rating, is_active) VALUES (@code, @tovar, @rating, @active)";
                    using var cmd = new SqliteCommand(sql, conn, tran);
                    cmd.Parameters.AddWithValue("@code", long.TryParse(s.Code, out long c) ? c : 0);
                    cmd.Parameters.AddWithValue("@tovar", long.TryParse(s.CodeTovar, out long ct2) ? ct2 : 0);
                    cmd.Parameters.AddWithValue("@rating", decimal.TryParse(s.Rating, out decimal r) ? r : 0);
                    cmd.Parameters.AddWithValue("@active", int.TryParse(s.IsActive, out int a) ? a : 0);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }

            if (loadData.ListActionHeader?.Count > 0)
            {
                foreach (var ah in loadData.ListActionHeader)
                {
                    string sql = "INSERT INTO action_header (date_started, date_end, num_doc, tip, barcode, persent, sum, comment) VALUES (@ds, @de, @nd, @tip, @bar, @p, @sum, @com)";
                    using var cmd = new SqliteCommand(sql, conn, tran);
                    cmd.Parameters.AddWithValue("@ds", ah.DateStarted ?? ""); cmd.Parameters.AddWithValue("@de", ah.DateEnd ?? "");
                    cmd.Parameters.AddWithValue("@nd", int.TryParse(ah.NumDoc, out int nd) ? nd : 0);
                    cmd.Parameters.AddWithValue("@tip", int.TryParse(ah.Tip, out int tip) ? tip : 0);
                    cmd.Parameters.AddWithValue("@bar", ah.Barcode ?? ""); cmd.Parameters.AddWithValue("@p", ah.Persent ?? "");
                    cmd.Parameters.AddWithValue("@sum", ah.sum ?? ""); cmd.Parameters.AddWithValue("@com", ah.Comment ?? "");
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }

            // Обновление даты
            await ExecNonQueryAsync("UPDATE date_sync SET tovar = @date WHERE id=1; INSERT INTO date_sync (id, tovar) SELECT 1, @date WHERE (SELECT changes() = 0);", conn, tran, ct, ("@date", DateTime.Now.ToString("dd.MM.yyyy HH:mm")));

            await tran.CommitAsync(ct);
            return (true, "");
        }
        catch (Exception ex)
        {
            await tran.RollbackAsync(ct);
            return (false, ex.Message);
        }
    }

    private async Task ExecNonQueryAsync(string sql, SqliteConnection conn, SqliteTransaction tran, CancellationToken ct, params (string, object)[] pars)
    {
        using var cmd = new SqliteCommand(sql, conn, tran);
        foreach (var p in pars) cmd.Parameters.AddWithValue(p.Item1, p.Item2);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task FinalizeLoadAsync(CancellationToken ct) => await Task.Delay(200, ct);

    #endregion

    #region Загрузка клиентов (load_bonus_clients)

    public async Task load_bonus_clients(bool show_message) => await load_bonus_clients_internal(show_message);

    private async Task load_bonus_clients_internal(bool show_message)
    {
        try
        {
            DS ds = await ServiceLocator.DsAsync(); ds.Timeout = 60000;
            string nick_shop = MainStaticClass.Nick_Shop.Trim();
            string code_shop = MainStaticClass.Code_Shop.Trim();
            string key = nick_shop + CryptorEngine.GetCountDay() + code_shop;
            bool needToLoadMore = true; int portionNumber = 1;

            while (needToLoadMore)
            {
                DateTime dt = last_date_download_bonus_clients();
                string data = CryptorEngine.Encrypt($"{nick_shop}|{dt.Ticks}|{code_shop}", true, key);
                string result_query = "-1";

                try { result_query = ds.GetDiscountClientsV8DateTime_NEW(nick_shop, data, "4"); }
                catch (Exception ex) { if (show_message) await DisplayAlert("Ошибка", ex.Message, "OK"); break; }

                if (result_query == "-1") break;

                string decrypt = CryptorEngine.Decrypt(result_query, true, key);
                Clients clients = JsonConvert.DeserializeObject<Clients>(decrypt);
                if (clients?.list_clients == null || clients.list_clients.Count == 0) break;

                using var conn = new SqliteConnection($"Data Source={DbPath}");
                await conn.OpenAsync();
                using var tran = await conn.BeginTransactionAsync() as SqliteTransaction;

                try
                {
                    foreach (var client in clients.list_clients)
                    {
                        // SQLite использует INSERT OR REPLACE вместо ON CONFLICT
                        string sql = "INSERT OR REPLACE INTO clients (code, phone, name, date_of_birth, its_work, reason_for_blocking, notify_security, last_server_sync) " +
                                     "VALUES (@code, @phone, @name, @dob, @work, @reason, @notify, @sync)";
                        using var cmd = new SqliteCommand(sql, conn, tran);
                        cmd.Parameters.AddWithValue("@code", client.code ?? "");
                        cmd.Parameters.AddWithValue("@phone", client.phone ?? "");
                        cmd.Parameters.AddWithValue("@name", string.IsNullOrWhiteSpace(client.name) ? client.phone : client.name);
                        cmd.Parameters.AddWithValue("@dob", ParseDateForDb(client.holiday)?.ToString("yyyy-MM-dd") ?? (object)DBNull.Value);
                        //cmd.Parameters.AddWithValue("@dob", (object)ParseDateForDb(client.holiday) ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@work", ParseSmallint(client.use_blocked));
                        cmd.Parameters.AddWithValue("@reason", client.reason_for_blocking ?? "");
                        cmd.Parameters.AddWithValue("@notify", ParseSmallint(client.notify_security));
                        cmd.Parameters.AddWithValue("@sync", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        await cmd.ExecuteNonQueryAsync();
                    }

                    string updateConstants = "UPDATE constants SET last_date_download_bonus_clients = @last_date WHERE rowid=1; INSERT INTO constants (last_date_download_bonus_clients) SELECT @last_date WHERE (SELECT changes() = 0);";
                    using var cmdConst = new SqliteCommand(updateConstants, conn, tran);
                    cmdConst.Parameters.AddWithValue("@last_date", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    await cmdConst.ExecuteNonQueryAsync();

                    await tran.CommitAsync();

                    if (clients.list_clients.Count < 50000) needToLoadMore = false;
                    else portionNumber++;
                }
                catch (Exception ex)
                {
                    await tran.RollbackAsync();
                    if (show_message) await DisplayAlert("Ошибка импорта", ex.Message, "OK");
                    break;
                }
            }
            if (show_message) await DisplayAlert("Успех", "Загрузка клиентов завершена", "OK");
        }
        catch (Exception ex) { Console.WriteLine($"Критическая ошибка: {ex}"); }
    }

    private DateTime? ParseDateForDb(string dateStr) 
        => DateTime.TryParse(dateStr, out DateTime res) ? res.Date : (DateTime?)null;
    private object ParseSmallint(string val) => short.TryParse(val, out short s) ? s : (short)0;

    private DateTime last_date_download_bonus_clients()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            using var cmd = new SqliteCommand("SELECT last_date_download_bonus_clients FROM constants LIMIT 1", conn);
            var res = cmd.ExecuteScalar()?.ToString();
            return DateTime.TryParse(res, out DateTime d) ? d : new DateTime(2000, 1, 1);
        }
        catch { return new DateTime(2000, 1, 1); }
    }

    #endregion

    #region Вспомогательные методы

    private string EscapeSql(string input) => string.IsNullOrEmpty(input) ? "" : input.Replace("'", "''");

    public DateTime last_date_download_tovars()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            using var cmd = new SqliteCommand("SELECT tovar FROM date_sync LIMIT 1", conn);
            var res = cmd.ExecuteScalar()?.ToString();
            return DateTime.TryParse(res, out DateTime d) ? d : new DateTime(2000, 1, 1);
        }
        catch { return new DateTime(2000, 1, 1); }
    }

    #endregion

    #region UI Методы

    private void UpdateLastSyncDate()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            DateTime lastDate = last_date_download_tovars();
            LastUpdateText.Text = lastDate > new DateTime(2001, 1, 1) ? $"Последняя загрузка: {lastDate:dd.MM.yyyy HH:mm}" : "Последняя загрузка: данных еще нет";
        });
    }

    private async Task UpdateProgressAsync(string message, int progress)
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            StatusText.Text = message;
            ProgressBar1.Progress = progress / 100.0;
            ProgressPercent.Text = $"{progress}%";
            LogText.Text += $"\n{message}";
        });
        await Task.Delay(50);
    }

    private async Task HandleTimeoutAsync()
    {
        _cancellationTokenSource?.Cancel();
        await DisplayAlert("Таймаут", $"Загрузка превысила лимит времени ({_loadTimeout.TotalMinutes} мин)", "OK");
    }

    private async Task HandleLoadResultAsync(bool success, string errorMessage)
    {
        if (success) await DisplayAlert("Успех", "Загрузка данных успешно завершена", "OK");
        else await DisplayAlert("Ошибка", $"Не удалось выполнить загрузку:\n{errorMessage}", "OK");
    }

    protected override bool OnBackButtonPressed()
    {
        if (_isLoading)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                bool cancel = await DisplayAlert("Подтверждение", "Идет загрузка. Отменить?", "Да", "Нет");
                if (cancel) { _userCancelled = true; _cancellationTokenSource?.Cancel(); }
            });
            return true; // Предотвращаем закрытие
        }
        return base.OnBackButtonPressed();
    }

    #endregion

    #endregion // Основная логика загрузки
    
    
    private async void BtnUpload_Clicked(object sender, EventArgs e)
{
    if (_isLoading) return;
    
    bool confirm = await DisplayAlert("Подтверждение", "Выполнить выгрузку чеков на сервер?", "Да", "Нет");
    if (!confirm) return;

    try
    {
        BtnUpload.Text = "Выгрузка...";
        BtnUpload.IsEnabled = false;

        await Task.Run(async () => await UploadChecksAsync());

        await DisplayAlert("Успех", "Выгрузка чеков завершена", "OK");
    }
    catch (Exception ex)
    {
        await DisplayAlert("Ошибка", ex.Message, "OK");
    }
    finally
    {
        BtnUpload.Text = "Выгрузить чеки на сервер";
        BtnUpload.IsEnabled = true;
    }
}

    private async Task UploadChecksAsync()
    {
        var salesPortions = new SalesPortions
        {
            Shop = MainStaticClass.Nick_Shop?.Trim(),
            Guid = MainStaticClass.Code_Shop?.Trim(),
            Version = MainStaticClass.GetProductVersion().Replace(".", ""),
            ListSalesPortionsHeader = new List<SalesPortionsHeader>(),
            ListSalesPortionsTable = new List<SalesPortionsTable>()
        };

        var guidsToSend = new List<string>();

        using (var conn = MainStaticClass.GetLocalSQLiteConnection())
        {
            await conn.OpenAsync();

            // 1. Получаем GUID неотправленных чеков
            string selectGuids =
                "SELECT DISTINCT guid FROM checks_header WHERE is_sent = 0 AND guid IS NOT NULL AND its_deleted < 2";
            using (var cmd = new SqliteCommand(selectGuids, conn))
            {
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        guidsToSend.Add(reader.GetString(0));
                    }
                }
            }

            if (guidsToSend.Count == 0) return; // Нечего отправлять

            // Формируем строку IN (@g0, @g1...)
            string inClause = string.Join(",", guidsToSend.Select((g, i) => $"@g{i}"));

            // 2. Читаем шапки чеков
            string headerQuery = $@"SELECT document_number, cash_desk_number, client, discount, cash, check_type, 
                                       date_time_start, date_time_write, its_deleted, autor, comment, 
                                       its_print, its_print_p, extra, guid 
                                FROM checks_header WHERE guid IN ({inClause})";

            using (var cmd = new SqliteCommand(headerQuery, conn))
            {
                for (int i = 0; i < guidsToSend.Count; i++) cmd.Parameters.AddWithValue($"@g{i}", guidsToSend[i]);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        // Безопасное получение строк (возвращает "", если в базе NULL)
                        string GetSafeString(string colName)
                        {
                            int ordinal = reader.GetOrdinal(colName);
                            return reader.IsDBNull(ordinal) ? "" : reader.GetValue(ordinal).ToString();
                        }

                        // Безопасное получение числа (возвращает 0, если в базе NULL)
                        int GetSafeInt(string colName)
                        {
                            int ordinal = reader.GetOrdinal(colName);
                            if (reader.IsDBNull(ordinal)) return 0;

                            var val = reader.GetValue(ordinal);
                            // SQLite может вернуть long или int, конвертируем безопасно
                            return Convert.ToInt32(val);
                        }

                        var header = new SalesPortionsHeader
                        {
                            Shop = salesPortions.Shop,
                            Num_doc = GetSafeString("document_number"),
                            Num_cash = GetSafeString("cash_desk_number"),
                            Client = GetSafeString("client"),
                            // Если вернулась пустая строка, заменяем на "0", иначе меняем запятые на точки
                            Discount =
                                (GetSafeString("discount") == "" ? "0" : GetSafeString("discount")).Replace(",", "."),
                            Sum = (GetSafeString("cash") == "" ? "0" : GetSafeString("cash")).Replace(",", "."),
                            Check_type = GetSafeString("check_type") == "" ? "0" : GetSafeString("check_type"),
                            Date_time_start = GetSafeString("date_time_start"),
                            Date_time_write = GetSafeString("date_time_write"),
                            Its_deleted = GetSafeString("its_deleted") == "" ? "0" : GetSafeString("its_deleted"),
                            Autor = GetSafeString("autor"),
                            Comment = GetSafeString("comment"),

                            // ✅ Безопасное чтение флагов печати
                            Its_print = (GetSafeInt("its_print") == 1 && GetSafeInt("its_print_p") == 1) ? "1" : "0",

                            Extra = GetSafeString("extra") == "" ? "0" : GetSafeString("extra"),
                            Guid = GetSafeString("guid")
                        };
                        salesPortions.ListSalesPortionsHeader.Add(header);
                    }
                }
            }

            // 3. Читаем строки чеков
            string tableQuery = $@"SELECT document_number, tovar_code, quantity, price, price_at_a_discount, 
                                      sum, sum_at_a_discount, item_marker, guid 
                               FROM checks_table WHERE guid IN ({inClause})";

            using (var cmd = new SqliteCommand(tableQuery, conn))
            {
                for (int i = 0; i < guidsToSend.Count; i++) cmd.Parameters.AddWithValue($"@g{i}", guidsToSend[i]);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var table = new SalesPortionsTable
                        {
                            Shop = salesPortions.Shop,
                            Num_doc = reader["document_number"].ToString(),
                            Tovar = reader["tovar_code"].ToString(),
                            Quantity = reader["quantity"]?.ToString().Replace(",", ".") ?? "0",
                            Price = reader["price"]?.ToString().Replace(",", ".") ?? "0",
                            Price_d = reader["price_at_a_discount"]?.ToString().Replace(",", ".") ?? "0",
                            Sum = reader["sum"]?.ToString().Replace(",", ".") ?? "0",
                            Sum_d = reader["sum_at_a_discount"]?.ToString().Replace(",", ".") ?? "0",
                            MarkingCode = reader["item_marker"]?.ToString() ?? "",
                            Guid = reader["guid"]?.ToString()
                        };
                        salesPortions.ListSalesPortionsTable.Add(table);
                    }
                }
            }
        }

        // 4. Отправляем на сервер
        if (salesPortions.ListSalesPortionsHeader.Count == 0) return;

        string key = MainStaticClass.Nick_Shop.Trim() + CryptorEngine.GetCountDay() + MainStaticClass.Code_Shop.Trim();
        string data = JsonConvert.SerializeObject(salesPortions, Formatting.Indented,
            new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        string data_crypt = CryptorEngine.Encrypt(data, true, key);

        DS ds = await ServiceLocator.DsAsync();
        ds.Timeout = 80000;

        bool result = false;
        try
        {
            result = ds.UploadDataOnSalesPortionJsonAvalon(MainStaticClass.Nick_Shop, data_crypt, "10");
                //MainStaticClass.GetWorkSchema.ToString());
        }
        catch (Exception ex)
        {
            //MainStaticClass.WriteRecordErrorLog(ex, 0, MainStaticClass.CashDeskNumber, "UploadChecksAsync");
            throw new Exception("Ошибка связи с сервером: " + ex.Message);
        }

        // 5. Если успешно, помечаем как отправленные
        if (result)
        {
            using (var conn = MainStaticClass.GetLocalSQLiteConnection())
            {
                await conn.OpenAsync();
                string inClause = string.Join(",", guidsToSend.Select((g, i) => $"@g{i}"));
                string updateQuery = $"UPDATE checks_header SET is_sent = 1 WHERE guid IN ({inClause})";

                using (var cmd = new SqliteCommand(updateQuery, conn))
                {
                    for (int i = 0; i < guidsToSend.Count; i++) cmd.Parameters.AddWithValue($"@g{i}", guidsToSend[i]);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        else
        {
            throw new Exception("Сервер вернул ошибку при выгрузке чеков.");
        }
    }
}