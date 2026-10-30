using Microsoft.Data.Sqlite;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CashPlace.Services.Actions;  

namespace CashPlace;

public partial class CheckPage : ContentPage
{
    public ObservableCollection<ProductItem> ProductsList { get; set; } = new();
    
    private bool _isNewCheck = true;
    private string _existingCheckDate = "";
    private long _numdoc = 0;
    private DateTime _checkStartTime;
    private bool _isPayProcessing = false; 
    private int _payType = 0;   // 0 = наличные, 1 = QR-код
    private string _clientCode = "";         // код клиента → checks_header.client
    private bool _clientHasDiscount = false; // клиент найден и активен → скидка 5%
    
    // Добавляем публичное свойство, чтобы XAML мог управлять видимостью кнопок
    public bool IsNewCheck => _isNewCheck;
    
    // ★ Список причин — как в десктопном ReasonsDeletionCheck ★
    private static readonly string[] DeletionReasons =
    {
        "Проверка акции.",
        "Несрабатывание акции, переоценки, уценки.",
        "Ошибка магазина.",
        "Техническая ошибка.",
        "Клиента не устроила цена.",
        "Клиенту не хватило денег.",
        "IT-отдел",
        "Иные причины."
    };

    // ★ GUID чека — один на всё время жизни чека (как на десктопе):
    //   по нему строки из deleted_items привязываются к checks_header
    private readonly string _checkGuid = Guid.NewGuid().ToString();

    // ★ комментарий кассира (комментарий + номера пятитысячных купюр) → checks_header.comment
    private string _cashierComment = "";

    // ★ десктоп спрашивает причину и при УМЕНЬШЕНИИ количества (type_of_operation=1).
    //   На планшете по умолчанию ВЫКЛ, чтобы не раздражать кассира; true = полный паритет
    private static readonly bool AskReasonOnDecrease = false;

    // Конструктор для НОВОГО чека
    public CheckPage()
    {
        InitializeComponent();
        BindingContext = this;
        
        NumCashLabel.Text = $"Касса № "+MainStaticClass.CashDeskNumber.ToString();
        DateTimeLabel.Text = DateTime.Now.ToString("dd.MM HH:mm");
        UserLabel.Text = "Тестовый опер";
        
        _checkStartTime = DateTime.Now; 
        
        SetPayType(0);   // ★ по умолчанию — наличные (подсвечиваем кнопку)
    }
    
    // Срабатывает при появлении страницы на экране
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        
        if (_isNewCheck)
        {
            // ★ ПОЛУЧАЕМ НОМЕР ЧЕКА ИЗ БАЗЫ ★
            await GetNextDocumentNumberAsync();
            
            SearchProductEntry.Focus();
        }
    }
    
    // ★ ГЕНЕРАЦИЯ ПОРЯДКОВОГО НОМЕРА ★
    private async Task GetNextDocumentNumberAsync()
    {
        try
        {
            using (var conn = MainStaticClass.GetLocalSQLiteConnection())
            {
                await conn.OpenAsync();
                string query = "SELECT MAX(document_number) FROM checks_header";
                using (var cmd = new SqliteCommand(query, conn))
                {
                    var result = await cmd.ExecuteScalarAsync();
                    
                    if (result != null && result != DBNull.Value)
                    {
                        _numdoc = Convert.ToInt64(result) + 1;
                    }
                    else
                    {
                        _numdoc = 1;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Ошибка получения номера чека: {ex.Message}");
            _numdoc = new Random().Next(1, 9999);
        }
    }
    
    // Конструктор для ПРОСМОТРА старого чека
    public CheckPage(string documentNumber, DateTime dateTimeWrite)
    {
        InitializeComponent();
        BindingContext = this;
        
        _isNewCheck = false;
        _existingCheckDate = dateTimeWrite.ToString("yyyy-MM-dd HH:mm:ss");
        
        Title = $"Просмотр чека №{documentNumber}";
        
        // Блокируем редактирование
        SearchProductEntry.IsEnabled = false;
        PayButton.IsVisible = false;
        PayTypeLayout.IsVisible = false;   // ★ вида оплаты в старом чеке не показываем
        ClientEntry.IsEnabled = false;   // в старом чеке клиента не меняем
        DeleteCheckButton.IsVisible = false;
        
        NumCashLabel.Text = $"Касса № "+MainStaticClass.CashDeskNumber.ToString();
        DateTimeLabel.Text = dateTimeWrite.ToString("dd.MM HH:mm");
        
        if (long.TryParse(documentNumber, out long docNum))
        {
            _numdoc = docNum;
            LoadExistingCheck();
            LoadClientOfExistingCheck(docNum);
        }
    }
    
    // ★ Показ клиента из истории чека + комментарий чека ★
    private async void LoadClientOfExistingCheck(long doc)
    {
        try
        {
            using (var conn = MainStaticClass.GetLocalSQLiteConnection())
            {
                await conn.OpenAsync();

                string code = "";
                string savedComment = "";
                using (var cmd = new SqliteCommand(
                           "SELECT client, comment FROM checks_header WHERE document_number=@d", conn))
                {
                    cmd.Parameters.AddWithValue("@d", doc);
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        if (await r.ReadAsync())
                        {
                            code = r.IsDBNull(0) ? "" : r.GetValue(0).ToString();
                            savedComment = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                        }
                    }
                }

                // ★ показываем комментарий чека (в т.ч. >>причину<< удаления)
                if (!string.IsNullOrWhiteSpace(savedComment))
                {
                    CommentLabel.Text = $"✎ {savedComment}";
                    CommentLabel.IsVisible = true;
                }

                if (!string.IsNullOrWhiteSpace(code))
                {
                    _clientCode = code;
                    ClientEntry.Text = code;

                    string name = code;
                    using (var cmd2 = new SqliteCommand(
                               "SELECT name FROM clients WHERE code=@c LIMIT 1", conn))
                    {
                        cmd2.Parameters.AddWithValue("@c", code);
                        var n = await cmd2.ExecuteScalarAsync();
                        if (n != null) name = n.ToString();
                    }
                    ClientLabel.Text = $"Клиент: {name}";
                }
            }
        }
        catch { }
    }

    // Обработка сканера
    private async void SearchProductEntry_Completed(object sender, EventArgs e)
    {
        string barcode = SearchProductEntry.Text?.Trim();
        if (string.IsNullOrEmpty(barcode)) return;

        SearchProductEntry.Text = "";
        await FindAndAddProductAsync(barcode);
    }

    private async Task FindAndAddProductAsync(string barcode)
    {
        try
        {
            // Проверка весового товара (пример: 23... код)
            bool isFromScales = false;
            double weightFromScales = 0;
            if (barcode.Length == 13 && barcode.StartsWith("23"))
            {
                weightFromScales = Math.Round(double.Parse(barcode.Substring(8, 4)) / 1000, 3);
                barcode = Convert.ToInt32(barcode.Substring(2, 6)).ToString();
                isFromScales = true;
            }

            ProductItem foundProduct = null;

            // Ищем в БД SQLite
            using (var conn = MainStaticClass.GetLocalSQLiteConnection())
            {
                await conn.OpenAsync();
                
                string query;
                using var cmd = new SqliteCommand { Connection = conn };

                // ЛОГИКА КАК В POSTGRES: Если код длиннее 6 символов — ищем в таблице barcode
                if (barcode.Length > 6)
                {
                    query = @"
                        SELECT t.code, t.name, t.retail_price, t.its_certificate, t.its_marked, t.cdn_check, 
                               t.fractional, t.refusal_of_marking, t.rr_not_control_owner 
                        FROM barcode b 
                        LEFT JOIN tovar t ON b.tovar_code = t.code 
                        WHERE b.barcode = @searchVal 
                          AND t.its_deleted = 0 
                          AND t.retail_price <> 0 
                        LIMIT 1";
                    cmd.Parameters.AddWithValue("@searchVal", barcode);
                }
                // ИНАЧЕ ИЩЕМ ПРЯМО В ТАБЛИЦЕ tovar ПО КОДУ
                else
                {
                    query = @"
                        SELECT t.code, t.name, t.retail_price, t.its_certificate, t.its_marked, t.cdn_check, 
                               t.fractional, t.refusal_of_marking, t.rr_not_control_owner 
                        FROM tovar t 
                        WHERE t.its_deleted = 0 
                          AND t.retail_price <> 0 
                          AND t.code = @searchVal 
                        LIMIT 1";
                    cmd.Parameters.AddWithValue("@searchVal", long.TryParse(barcode, out long c) ? c : 0);
                }

                cmd.CommandText = query;

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        foundProduct = new ProductItem
                        {
                            Code = Convert.ToInt32(reader.GetValue(0)),
                            Tovar = reader.GetString(1),
                            Price = Convert.ToDecimal(reader.GetValue(2)),        // полная цена
                            PriceAtDiscount = Convert.ToDecimal(reader.GetValue(2)),
                            IsFractional = Convert.ToInt32(reader.GetValue(6)) == 1
                        };
                    }
                }
            }

            if (foundProduct == null)
            {
                PlayErrorSound(); // ★ ИГРАЕМ ЗВУК
                await DisplayAlert("Ошибка", "Товар не найден", "OK");
                return;
            }

            // Обработка веса
            if (isFromScales && foundProduct.IsFractional)
            {
                foundProduct.Quantity = Convert.ToDecimal(weightFromScales);
                ProductsList.Add(foundProduct);
            }
            else if (foundProduct.IsFractional)
            {
                // Запрос веса вручную
                string result = await DisplayPromptAsync("Весовой товар", foundProduct.Tovar, initialValue: "0.001");
                if (decimal.TryParse(result, out decimal weight) && weight > 0)
                    foundProduct.Quantity = weight;
                else
                    return; // Отмена
                ProductsList.Add(foundProduct);
            }
            else
            {
                // Обычный товар
                var existing = ProductsList.FirstOrDefault(p => p.Code == foundProduct.Code);
                if (existing != null)
                {
                    existing.Quantity++;
                }
                else
                {
                    foundProduct.Quantity = 1;
                    ProductsList.Add(foundProduct);
                }
            }

            // ★ Пересчёт сразу при добавлении товара: акции + 5% клиента (без всплывающих сообщений)
            await RecalcIfNewCheck(false);
            ProductsCollectionView.ScrollTo(ProductsList.Count - 1);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }
    }

    private async void OnIncreaseQuantity(object sender, EventArgs e)
    {
        // Защита от изменения в старом чеке
        if (!_isNewCheck) return; 

        if (sender is Button btn && btn.BindingContext is ProductItem product)
        {
            if (!product.IsMarked && !product.IsFractional)
            {
                product.Quantity++;
                await RecalcIfNewCheck(false);   // ★ акционные условия могут измениться от количества
            }
            else
            {
                ShowQuantityDialog(product);
            }
        }
    }

    private async void OnDecreaseQuantity(object sender, EventArgs e)
    {
        // Защита от изменения в старом чеке
        if (!_isNewCheck) return; 

        if (sender is Button btn && btn.BindingContext is ProductItem product)
        {
            if (product.Quantity > 1)
            {
                // ★ десктоп спрашивает причину при уменьшении количества (type_of_operation=1)
                if (AskReasonOnDecrease)
                {
                    string reason = await AskDeletionReasonAsync("Причина уменьшения количества");
                    if (string.IsNullOrWhiteSpace(reason)) return;   // отмена — не уменьшаем

                    await InsertDeletedItemAsync(product.Code, 1, 1, reason);
                }

                product.Quantity--;
                await RecalcIfNewCheck(false);   // ★ акционные условия могут измениться от количества
            }
        }
    }

    private async void ShowQuantityDialog(ProductItem product)
    {
        string result = await DisplayPromptAsync("Количество", product.Tovar, initialValue: product.Quantity.ToString());
        if (decimal.TryParse(result, out decimal newQty) && newQty > 0)
        {
            product.Quantity = newQty;
            await RecalcIfNewCheck(false);
        }
    }

    private void UpdateTotalSum()
    {
        decimal total = ProductsList.Sum(p => p.SumAtDiscount);
        TotalSumLabel.Text = $"{total:F2} ₽";
    }
    
    // ★ Удаление позиции из чека — с выбором причины ★
    private async void OnDeleteProduct(object sender, EventArgs e)
    {
        if (!_isNewCheck) return;

        if (sender is Button btn && btn.BindingContext is ProductItem product)
        {
            bool confirm = await DisplayAlert("Удаление позиции",
                $"Удалить «{product.Tovar}» из чека?", "Да", "Нет");
            if (!confirm) return;

            // ★ модальный диалог: null = «Отмена» → строка НЕ удаляется
            string reason = await AskDeletionReasonAsync("Причина удаления строки");
            if (string.IsNullOrWhiteSpace(reason)) return;

            // ★ как на десктопе: удалённая строка уходит в ОТДЕЛЬНУЮ таблицу deleted_items
            await InsertDeletedItemAsync(product.Code, product.Quantity, 0, reason);

            ProductsList.Remove(product);

            // ★ пересчёт документа: акция могла завязаться на удалённую строку.
            //   Если строка была последней — список пуст, просто обнуляем итого
            if (ProductsList.Count > 0)
                await RecalculateCheckAsync(false);
            else
                UpdateTotalSum();
        }
    }

// ★ показывает модальный диалог причин; null = «Отмена»/«Назад»
    private async Task<string?> AskDeletionReasonAsync(string title)
    {
        var page = new ReasonsDeletionPage(title, DeletionReasons);
        await Navigation.PushModalAsync(page);
        return await page.WaitAsync();
    }

    // ★ запись удалённой строки/количества в отдельную таблицу — как deleted_items на десктопе.
    //   type_of_operation: 0 = удаление строки, 1 = уменьшение количества
    private async Task InsertDeletedItemAsync(int tovarCode, decimal quantity, int typeOfOperation, string reason)
    {
        try
        {
            using (var conn = MainStaticClass.GetLocalSQLiteConnection())
            {
                await conn.OpenAsync();
                await EnsureSchemaAsync(conn);   // ★ deleted_items и guid-колонки точно существуют
                string query = @"INSERT INTO deleted_items
                    (num_doc, num_cash, date_time_start, date_time_action, tovar, quantity,
                     type_of_operation, guid, autor, reason)
                    VALUES (@doc, @cash, @start, @action, @tovar, @qty, @type, @guid, @autor, @reason)";

                using (var cmd = new SqliteCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@doc", _numdoc);
                    cmd.Parameters.AddWithValue("@cash", MainStaticClass.CashDeskNumber.ToString());
                    cmd.Parameters.AddWithValue("@start", _checkStartTime.ToString("yyyy-MM-dd HH:mm:ss"));
                    cmd.Parameters.AddWithValue("@action", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    cmd.Parameters.AddWithValue("@tovar", tovarCode.ToString());
                    cmd.Parameters.AddWithValue("@qty", quantity.ToString("F2").Replace(",", "."));
                    cmd.Parameters.AddWithValue("@type", typeOfOperation);
                    cmd.Parameters.AddWithValue("@guid", _checkGuid);
                    cmd.Parameters.AddWithValue("@autor", "111111222222");   // как в checks_header
                    cmd.Parameters.AddWithValue("@reason", reason);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка записи удалённой строки", ex.Message, "OK");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  МИГРАЦИЯ СХЕМЫ: таблица deleted_items + колонки guid.
    //  Выполняется один раз за запуск приложения; все операции идемпотентны
    //  (IF NOT EXISTS / проверка PRAGMA table_info), поэтому безопасны
    //  и на свежей базе, и на той, где всё уже создано.
    // ══════════════════════════════════════════════════════════════════
    private static bool _schemaEnsured = false;

    private static async Task EnsureSchemaAsync(SqliteConnection conn)
    {
        if (_schemaEnsured) return;

        // 1) отдельная таблица удалённых строк — как deleted_items на десктопе
        using (var cmd = new SqliteCommand(@"
            CREATE TABLE IF NOT EXISTS deleted_items (
                id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                num_doc            INTEGER,       -- номер документа (= checks_header.document_number)
                num_cash           TEXT,          -- касса
                date_time_start    TEXT,          -- начало чека (как в checks_header)
                date_time_action   TEXT,          -- момент удаления строки
                tovar              TEXT,          -- код товара
                quantity           TEXT,          -- сколько удалено
                type_of_operation  INTEGER,       -- 0 = удалена строка, 1 = уменьшено количество
                guid               TEXT,          -- GUID чека → checks_header.guid
                autor              TEXT,          -- оператор (как в checks_header)
                reason             TEXT,          -- причина удаления
                is_sent            INTEGER DEFAULT 0
            );", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // 2) колонки, которых могло не быть в базе, созданной до этой версии
        await AddColumnIfMissingAsync(conn, "checks_header", "guid", "TEXT");
        await AddColumnIfMissingAsync(conn, "checks_table", "guid", "TEXT");
        await AddColumnIfMissingAsync(conn, "checks_table", "promo_description", "TEXT");
        await AddColumnIfMissingAsync(conn, "checks_header", "pay_type", "INTEGER");

        _schemaEnsured = true;
    }

    // ALTER TABLE ... ADD COLUMN, если колонки ещё нет (PRAGMA table_info)
    private static async Task AddColumnIfMissingAsync(SqliteConnection conn, string table, string column, string type)
    {
        using (var check = new SqliteCommand($"PRAGMA table_info({table});", conn))
        using (var reader = await check.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                    return;   // колонка уже есть — ничего не делаем
        }

        using (var alter = new SqliteCommand($"ALTER TABLE {table} ADD COLUMN {column} {type};", conn))
            await alter.ExecuteNonQueryAsync();
    }

    // ★ КОММЕНТАРИЙ КАССИРА: комментарий + номера купюр → checks_header.comment
    private async void OnCommentButtonClicked(object sender, EventArgs e)
    {
        string result = await DisplayPromptAsync("Комментарий к чеку",
            "Комментарий и номера купюр (например: 5000 № ab12345678)",
            initialValue: _cashierComment,
            maxLength: 200);

        if (result == null) return;   // отмена — ничего не меняем

        _cashierComment = result.Trim();
        UpdateCommentLabel();
    }

    private void UpdateCommentLabel()
    {
        bool has = !string.IsNullOrWhiteSpace(_cashierComment);
        CommentLabel.Text = has ? $"✎ {_cashierComment}" : "";
        CommentLabel.IsVisible = has;
    }

// ★ Удаление чека: клиент передумал ДО оплаты ★
    private async void OnDeleteCheckClicked(object sender, EventArgs e)
    {
        if (!_isNewCheck || ProductsList.Count == 0) return;
        if (_isPayProcessing) return;

        bool confirm = await DisplayAlert("Удаление чека",
            "Удалить текущий чек целиком?", "Да", "Нет");
        if (!confirm) return;

        // ★ модальный диалог: null = «Отмена» → чек НЕ удаляется
        string reason = await AskDeletionReasonAsync("Причина удаления чека");
        if (string.IsNullOrWhiteSpace(reason)) return;

        try
        {
            _isPayProcessing = true;

            // ★ удалённый чек: its_deleted=1 — как на десктопе (виден в списке, уходит на сервер).
            //   Статус 2 зарезервирован под «в процессе» — такие список прячет (its_deleted < 2).
            // ★ причина — в comment с маркерами >>...<< (так делает десктоп),
            //   комментарий кассира сохраняется перед ней; its_deleted=1, is_sent=0
            string headerComment = (_cashierComment + " >>" + reason + "<<").Trim();
            bool isWritten = await WriteCheckToDatabaseAsync(false, _payType, headerComment);
            if (!isWritten) return;

            ProductsList.Clear();
            _clientCode = "";
            _clientHasDiscount = false;
            ClientEntry.Text = "";
            ClientEntry.IsVisible = true; // ★ вернуть поле карты
            ClientLabel.Text = "";
            UpdateTotalSum();

            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }
        finally
        {
            _isPayProcessing = false;
        }
    }

    // ★ Выбор вида оплаты: наличные / QR ★
    private void OnPayTypeCash(object sender, EventArgs e) => SetPayType(0);
    private void OnPayTypeQr(object sender, EventArgs e) => SetPayType(1);

    private void SetPayType(int type)
    {
        _payType = type;
        // выбранный — синий, невыбранный — серый
        PayTypeCashBtn.BackgroundColor = type == 0 ? Color.FromArgb("#2196F3") : Colors.LightGray;
        PayTypeCashBtn.TextColor = type == 0 ? Colors.White : Colors.Black;
        PayTypeQrBtn.BackgroundColor = type == 1 ? Color.FromArgb("#2196F3") : Colors.LightGray;
        PayTypeQrBtn.TextColor = type == 1 ? Colors.White : Colors.Black;
    }
    
    // ★ ПОИСК КЛИЕНТА ПО КАРТЕ — логика десктопа ProcessClientDiscount ★
    private async void ClientEntry_Completed(object sender, EventArgs e)
    {
        string input = ClientEntry.Text?.Trim() ?? "";

        _clientCode = "";
        _clientHasDiscount = false;
        ClientLabel.Text = "";

        if (string.IsNullOrEmpty(input))
        {
            await RecalcIfNewCheck();   // карта стёрта — убираем скидку из чека
            return;
        }

        if (input.Length != 10 && input.Length != 13)
        {
            PlayErrorSound();
            await DisplayAlert("Карта клиента", "Нужно 10 или 13 цифр (телефон или код карты)", "OK");
            await RecalcIfNewCheck();
            return;
        }

        try
        {
            using (var conn = MainStaticClass.GetLocalSQLiteConnection())
            {
                await conn.OpenAsync();

                string query = input.StartsWith("9")
                    ? "SELECT code, name FROM clients WHERE phone = @v AND its_work = 1 LIMIT 1"
                    : "SELECT code, name FROM clients WHERE code  = @v AND its_work = 1 LIMIT 1";

                using (var cmd = new SqliteCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@v", input);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            _clientCode = reader.GetValue(0).ToString();
                            _clientHasDiscount = true;
                            ClientLabel.Text = $"Клиент: {reader.GetValue(1)}";
                            ClientEntry.IsVisible = false; 
                        }
                    }
                }
            }

            if (!_clientHasDiscount)
            {
                // ★ Послабление десктопа: маска телефона «9XXXXXXXXX».
                // Клиента в базе нет, но номер всё равно идёт в чек (checks_header.client)
                // и выгружается на промежуточный сервер «как есть». Без скидки 5%.
                if (input.Length == 10 && input[0] == '9' && input.All(char.IsDigit))
                {
                    _clientCode = input;
                    ClientLabel.Text = $"Телефон: {input} (клиент не найден, без скидки)";
                }
                else
                {
                    PlayErrorSound();
                    await DisplayAlert("Карта клиента", "Клиент не найден или карта заблокирована", "OK");
                }
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }

        await RecalcIfNewCheck();   // применили или убрали скидку — пересчитали чек
    }

    private async void PayButton_Clicked(object sender, EventArgs e)
    {
        if (_isPayProcessing)
            return;

        if (ProductsList.Count == 0)
        {
            await DisplayAlert("Внимание", "Чек пуст", "OK");
            return;
        }

        try
        {
            _isPayProcessing = true;

            // ═══ РАСЧЁТ АКЦИЙ + КЛИЕНТСКАЯ СКИДКА — только для нового чека ═══
            if (_isNewCheck)
            {
                await RecalculateCheckAsync();
            }

            // ═══ ОПЛАТА: вид оплаты выбран кнопкой на экране (_payType) ═══
            decimal total = Math.Round(ProductsList.Sum(p => p.SumAtDiscount), 2);

            bool confirm = await DisplayAlert("Оплата",
                $"Сумма к оплате: {total:F2} ₽. Подтвердить?", "Да", "Нет");
            if (!confirm)
                return; // скидки остаются; повторное нажатие пересчитает от полных цен

            // ★ в comment — только комментарий кассира (причины удаления строк
            //   уже ушли в deleted_items в момент удаления)
            bool isWritten = await WriteCheckToDatabaseAsync(true, _payType, _cashierComment);
            
            if (isWritten)
            {
                await DisplayAlert("Успех",
                    _payType == 1 ? "Чек пробит. Оплата: QR-код" : "Чек пробит. Оплата: наличные",
                    "OK");
                await Navigation.PopAsync();
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }
        finally
        {
            _isPayProcessing = false;
        }
    }

    private void PlayErrorSound()
    {
        try
        {
            Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(300));

#if ANDROID
            var toneGen = new Android.Media.ToneGenerator(Android.Media.Stream.Notification, 100);
            toneGen.StartTone(Android.Media.Tone.SupError, 300);
            toneGen.Release();
#endif
        }
        catch { }
    }
    
    // ★ Пересчёт чека: акции + клиентская скидка 5% + перерисовка списка ★
    // Всегда считает от ПОЛНЫХ цен (PriceAtDiscount = Price на входе), поэтому
    // любое количество повторных пересчётов не задваивает скидки.
    private async Task RecalculateCheckAsync(bool showMessages = true)
    {
        var oldFlags = ProductsList
            .GroupBy(p => p.Code)
            .ToDictionary(g => g.Key, g => g.First());

        var lines = ProductsList.Select(p => new CheckLine
        {
            TovarCode = p.Code,
            TovarName = p.Tovar,
            Quantity = p.Quantity,
            Price = p.Price,
            PriceAtDiscount = p.Price,   // всегда от полной цены
            SumFull = Math.Round(p.Quantity * p.Price, 2),
            SumAtDiscount = Math.Round(p.Quantity * p.Price, 2),
            Marking = p.Mark ?? "0"
        }).ToList();

        ActionCalcResult result = await Task.Run(() =>
        {
            using (var conn = MainStaticClass.GetLocalSQLiteConnection())
            {
                conn.Open();
                var engine = new ProcessingOfActions(conn);
                return engine.Process(lines, _clientCode, new List<string>());
            }
        });

        // 5% — только на строки БЕЗ акций (правило десктопа)
        if (_clientHasDiscount)
        {
            foreach (var l in result.Lines)
            {
                if (l.Action == 0 && l.Gift == 0)
                {
                    l.PriceAtDiscount = Math.Round(l.Price - l.Price * 0.05m, 2, MidpointRounding.AwayFromZero);
                    l.SumFull = Math.Round(l.Quantity * l.Price, 2, MidpointRounding.AwayFromZero);
                    l.SumAtDiscount = Math.Round(l.Quantity * l.PriceAtDiscount, 2, MidpointRounding.AwayFromZero);
                }
            }
        }

        ProductsList.Clear();
        foreach (var l in result.Lines)
        {
            oldFlags.TryGetValue((int)l.TovarCode, out var old);
            ProductsList.Add(new ProductItem
            {
                Code = (int)l.TovarCode,
                Tovar = l.TovarName,
                Price = l.Price,
                Quantity = l.Quantity,
                PriceAtDiscount = l.PriceAtDiscount,
                IsFractional = old?.IsFractional ?? false,
                IsMarked = old?.IsMarked ?? false,
                Mark = old?.Mark ?? "0",
                Action = l.Action,
                Gift = l.Gift,
                PromoDescription = l.PromoDescription
            });
        }

        UpdateTotalSum();

        if (showMessages && result.Messages.Count > 0)
            await DisplayAlert("Акции", string.Join("\n", result.Messages), "OK");
    }

    private async Task RecalcIfNewCheck(bool showMessages = true)
    {
        if (!_isNewCheck) return;

        if (ProductsList.Count > 0)
            await RecalculateCheckAsync(showMessages);
        else
            UpdateTotalSum();   // ★ чек опустел — ИТОГО должно стать 0.00
    }

    
    private async Task<bool> WriteCheckToDatabaseAsync(bool isPaid, int payType, string headerComment = "")
    {
        try
        {
            // ★ GUID фиксирован на всё время жизни чека — deleted_items привязан к нему
            string checkGuid = _checkGuid;

            using (var conn = MainStaticClass.GetLocalSQLiteConnection())
            {
                await conn.OpenAsync();
                await EnsureSchemaAsync(conn);   // ★ guid-колонки точно существуют
                using (var tran = await conn.BeginTransactionAsync())
                {
                    using (var delCmd = new SqliteCommand(
                               "DELETE FROM checks_table WHERE document_number=@doc; DELETE FROM checks_header WHERE document_number=@doc;",
                               conn, (SqliteTransaction)tran))
                    {
                        delCmd.Parameters.AddWithValue("@doc", _numdoc);
                        await delCmd.ExecuteNonQueryAsync();
                    }

                    // ★ ИТОГИ: к оплате, без скидки и общая скидка по чеку
                    decimal totalCash = Math.Round(ProductsList.Sum(p => p.SumAtDiscount), 2);
                    decimal totalFull = Math.Round(ProductsList.Sum(p => Math.Round(p.Quantity * p.Price, 2)), 2);
                    decimal discount = Math.Round(totalFull - totalCash, 2);

                    string insertHeader = @"INSERT INTO checks_header 
                    (document_number, date_time_start, date_time_write, client, cash_desk_number, comment, cash, remainder, discount, autor, its_deleted, check_type, guid, is_sent, pay_type) 
                    VALUES (@doc, @start, @write, @client, @desk, @comment, @cash, @rem, @disc, @autor, @del, @type, @guid, 0, @paytype)";

                    using (var cmd = new SqliteCommand(insertHeader, conn, (SqliteTransaction)tran))
                    {
                        cmd.Parameters.AddWithValue("@doc", _numdoc);
                        cmd.Parameters.AddWithValue("@start", _checkStartTime.ToString("yyyy-MM-dd HH:mm:ss"));
                        cmd.Parameters.AddWithValue("@write", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        cmd.Parameters.AddWithValue("@client", _clientCode);
                        cmd.Parameters.AddWithValue("@comment", headerComment);
                        cmd.Parameters.AddWithValue("@cash", totalCash);
                        cmd.Parameters.AddWithValue("@desk", MainStaticClass.CashDeskNumber.ToString());
                        cmd.Parameters.AddWithValue("@rem", 0);
                        cmd.Parameters.AddWithValue("@disc", discount);
                        cmd.Parameters.AddWithValue("@autor", "111111222222");
                        // ★ 0 = оплачен; 1 = удалён (виден в списке, уходит на сервер);
                        //   2 = «в процессе» — на планшете не пишем, список его прячет
                        cmd.Parameters.AddWithValue("@del", isPaid ? 0 : 1);
                        cmd.Parameters.AddWithValue("@type", 0);
                        cmd.Parameters.AddWithValue("@guid", checkGuid);
                        cmd.Parameters.AddWithValue("@paytype", payType);   // ★ 0 = наличные, 1 = QR

                        await cmd.ExecuteNonQueryAsync();
                    }

                    foreach (var p in ProductsList)
                    {
                        string insertItem = @"INSERT INTO checks_table 
    (document_number, tovar_code, name, quantity, price, price_at_a_discount, sum, sum_at_a_discount, guid, action, gift, promo_description) 
    VALUES (@doc, @code, @name, @qty, @price, @price_disc, @sum, @sum_disc, @guid, @act, @gift, @promo)";

                        using (var cmd = new SqliteCommand(insertItem, conn, (SqliteTransaction)tran))
                        {
                            cmd.Parameters.AddWithValue("@doc", _numdoc);
                            cmd.Parameters.AddWithValue("@code", p.Code);
                            cmd.Parameters.AddWithValue("@name", p.Tovar);
                            cmd.Parameters.AddWithValue("@qty", p.Quantity);
                            cmd.Parameters.AddWithValue("@price", p.Price);
                            cmd.Parameters.AddWithValue("@price_disc", p.PriceAtDiscount);
                            cmd.Parameters.AddWithValue("@sum", Math.Round(p.Quantity * p.Price, 2));
                            cmd.Parameters.AddWithValue("@sum_disc", Math.Round(p.SumAtDiscount, 2));
                            cmd.Parameters.AddWithValue("@guid", checkGuid);
                            cmd.Parameters.AddWithValue("@act", p.Action);
                            cmd.Parameters.AddWithValue("@gift", p.Gift);
                            cmd.Parameters.AddWithValue("@promo", p.PromoDescription ?? "");

                            await cmd.ExecuteNonQueryAsync();
                        }
                    }

                    await tran.CommitAsync();
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка БД", ex.Message, "OK");
            return false;
        }
    }

    // ★ ВЫЗОВ КАМЕРЫ ДЛЯ СКАНИРОВАНИЯ ТОВАРА ★
    private async void OnScanClicked(object sender, EventArgs e)
    {
        var scannerPage = new ScannerPage(async (result) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                SearchProductEntry.Text = result;
                SearchProductEntry_Completed(SearchProductEntry, EventArgs.Empty);
            });
        });

        await Navigation.PushModalAsync(scannerPage);
    }

    private async void LoadExistingCheck()
    {
        try
        {
            using (var conn = MainStaticClass.GetLocalSQLiteConnection())
            {
                await conn.OpenAsync();
                string query = @"SELECT tovar_code, name, quantity, price_at_a_discount 
                                 FROM checks_table 
                                 WHERE document_number = @doc";
                
                using (var cmd = new SqliteCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@doc", _numdoc);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            ProductsList.Add(new ProductItem
                            {
                                Code = Convert.ToInt32(reader.GetValue(0)),
                                Tovar = reader.GetString(1),
                                Quantity = Convert.ToDecimal(reader.GetValue(2)),
                                Price = Convert.ToDecimal(reader.GetValue(3)),     
                                PriceAtDiscount = Convert.ToDecimal(reader.GetValue(3)) 
                            });
                        }
                    }
                }
            }
            UpdateTotalSum();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка загрузки", ex.Message, "OK");
        }
    }
}
