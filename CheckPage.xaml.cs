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

    // ══════════════════════════════════════════════════════════════════
    //  ★ РЕЖИМ ПРЕДПРОСМОТРА ОПЛАТЫ (свайп справа налево / кнопка «ПЕРЕЙТИ») ★
    //  Кассир набирает чек сырыми строками, затем одним жестом показывает
    //  покупателю «новую табличную часть» — окончательный расчёт тем же
    //  движком акций, которым чек пересчитает серверная обработка после выгрузки.
    //  Предпросмотр — ТОЛЬКО ЧТЕНИЕ: считается в памяти, ничего не пишет в БД
    //  и не меняет сырые строки (паритет с десктопом: состав чека меняется
    //  только после оплаты).
    // ══════════════════════════════════════════════════════════════════
    private bool _isPreviewMode = false;                // false = набивка, true = окно оплаты
    private List<ProductItem> _previewItems = new();    // окончательный расчёт (только в памяти)
    private decimal _previewTotal = 0m;                 // сумма к оплате по окончательному расчёту
    private decimal _previewTotalFull = 0m;             // сумма без скидок (для строки «скидка»)
    private bool _checkClosed = false;                  // true = чек оплачен и записан (страховка, если переход не сработал)

    // ★ ИТОГО при набивке: «X / Y ₽» — X: полная сумма с клиентской скидкой,
    //   но БЕЗ акций (сырые строки), Y: окончательная сумма С УЧЁТОМ АКЦИЙ.
    //   Y считает ТОТ ЖЕ движок ProcessingOfActions, что и окно оплаты, —
    //   в фоне, чтобы не задерживать набор. Пока движок работает, кассир мог
    //   изменить чек — устаревший результат выбрасывается по номеру генерации.
    private decimal _compositionPromoTotal = 0m;        // последняя рассчитанная сумма с акциями
    private bool _compositionPromoTotalValid = false;   // true = _compositionPromoTotal актуален
    private int _promoTotalGeneration = 0;              // счётчик фоновых расчётов (защита от гонок)

    // ══════════════════════════════════════════════════════════════════
    //  ★ АКЦИОННЫЕ ШТРИХКОДЫ — аналог F5 десктопа ★
    //  Ввод: кнопка «★ ШК АКЦИИ» или свайп вверх/вниз по экрану (вне списка товаров).
    //  Введённые ШК передаются в движок акций ПЕРВЫМИ (kind=1 — приоритетная
    //  сработка перед персональными и обычными акциями, как на десктопе).
    //  Список живёт, пока живёт чек: сохраняется при переходах в окно оплаты
    //  и обратно, очищается при начале нового чека и при удалении чека
    //  (как action_barcode_list.Clear() на десктопе). В БД не пишется —
    //  на десктопе тоже: сохраняется лишь результат сработки (номера акций).
    // ══════════════════════════════════════════════════════════════════
    private readonly List<string> _promoBarcodes = new List<string>();

    // ★ строки чека редактируемы только в новом чеке, только при набивке
    //   и пока чек не закрыт: XAML гасит +/−/✕ через
    //   IsEnabled="{Binding IsEditable, Source={x:Reference rootPage}}"
    public bool IsEditable => _isNewCheck && !_isPreviewMode && !_checkClosed;
    
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
    //   по нему строки из deleted_items привязываются к checks_header.
    //   НЕ readonly: «СОЗДАТЬ ЧЕК» после закрытия выдаёт НОВЫЙ guid новому чеку
    private string _checkGuid = Guid.NewGuid().ToString();

    // ★ комментарий кассира (комментарий + номера пятитысячных купюр) → checks_header.comment
    private string _cashierComment = "";

    // ★ десктоп спрашивает причину и при УМЕНЬШЕНИИ количества (type_of_operation=1).
    //   На планшете по умолчанию ВЫКЛ, чтобы не раздражать кассира; true = полный паритет
    private static readonly bool AskReasonOnDecrease = false;

    // ★ АКЦИИ ПРИ НАБОРЕ: false = ВЫКЛ (паритет с десктопом).
    //   Состав чека до оплаты неприкосновенен — строки сырые, по ценам справочника.
    //   Акции (в т.ч. 4 тип: подарки за 1 ₽ и разбиение строк) считает «потом»
    //   серверная/десктопная обработка. true = старое поведение (не рекомендуется).
    private static readonly bool ApplyActionsDuringComposition = false;

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
        ApplyModeVisuals();   // ★ стартовый режим — НАБИВКА (тексты кнопок, подсказка о свайпе)
        UpdatePromoBarcodesLabel();   // ★ список акционных ШК пуст — кнопка без счётчика
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
        PromoBarcodeBtn.IsVisible = false;   // акционные ШК вводятся только в новом чеке
        
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
        if (_isPreviewMode) return;   // в предпросмотре набор товаров закрыт

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
        // Защита: редактирование только в новом чеке и только при набивке
        if (!IsEditable) return;

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
        // Защита: редактирование только в новом чеке и только при набивке
        if (!IsEditable) return;

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

    // ★ Отрисовка строки ИТОГО при набивке: «X / Y ₽»,
    //   X — полная сумма чека с клиентской скидкой, но БЕЗ акций (сырые строки),
    //   Y — окончательная сумма С УЧЁТОМ АКЦИЙ (та, что станет «К ОПЛАТЕ»).
    //   Пока фоновый расчёт акций не готов (или не нужен) — показывается только X.
    private void RenderCompositionTotal()
    {
        decimal total = Math.Round(ProductsList.Sum(p => p.SumAtDiscount), 2, MidpointRounding.AwayFromZero);
        TotalSumLabel.Text = _compositionPromoTotalValid
            ? $"{total:F2} / {_compositionPromoTotal:F2} ₽"
            : $"{total:F2} ₽";
    }

    // ★ Обновление итогов при набивке. Строки изменились → прежняя «сумма с
    //   акциями» недействительна: мгновенно рисуем X и запускаем фоновый
    //   расчёт Y. keepPromoTotal=true — состав чека не менялся (возврат из
    //   окна оплаты): показываем уже посчитанный Y без повторного прогона движка.
    private void UpdateTotalSum(bool keepPromoTotal = false)
    {
        if (_isPreviewMode) return;   // в окне оплаты итогами управляет ApplyModeVisuals

        if (!_isNewCheck || _checkClosed || ProductsList.Count == 0)
        {
            _compositionPromoTotalValid = false;
            RenderCompositionTotal();
            return;
        }

        if (!keepPromoTotal)
        {
            _compositionPromoTotalValid = false;
            RenderCompositionTotal();
            _ = RecalcPromoTotalInBackgroundAsync();   // второе число придёт следом
        }
        else
        {
            RenderCompositionTotal();
        }
    }

    // ★ Фоновый расчёт «суммы с акциями» для строки ИТОГО. Считает ТОТ ЖЕ
    //   окончательный расчёт, что и окно оплаты (BuildFinalPreviewAsync):
    //   движок акций на КОПИИ строк + 5% клиента только на строки без акций.
    //   Поэтому второе число всегда совпадает с суммой, которую кассир
    //   назовёт покупателю в окне оплаты. Сырые строки и БД не трогаются.
    private async Task RecalcPromoTotalInBackgroundAsync()
    {
        int gen = ++_promoTotalGeneration;
        try
        {
            var (_, total, _, _) = await BuildFinalPreviewAsync();   // сообщения фонового предрасчёта не показываем

            if (gen != _promoTotalGeneration) return;              // чек уже изменился — результат устарел
            if (!_isNewCheck || _isPreviewMode || _checkClosed) return;

            _compositionPromoTotal = total;
            _compositionPromoTotalValid = true;
            RenderCompositionTotal();
        }
        catch (Exception ex)
        {
            // акции не посчитались — второе число просто не показываем
            Debug.WriteLine($"Предрасчёт акций для ИТОГО: {ex.Message}");
        }
    }
    
    // ★ Удаление позиции из чека — с выбором причины ★
    private async void OnDeleteProduct(object sender, EventArgs e)
    {
        if (!IsEditable) return;

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

        // ★ ВИДЫ ОПЛАТЫ (паритет с десктопной write_new_document / PostgreSQL):
        //   cash_money — наличные, non_cash_money — безнал (карта/QR).
        //   Промежуточный сервер выгружает их как Sum_cash и Sum_terminal
        //   (SendDataOnSalesPortions.getdata_h). *1 — дубли, на десктопе
        //   заполняются только для SystemTaxation 3/5; payment_by_sbp — флаг СБП.
        await AddColumnIfMissingAsync(conn, "checks_header", "cash_money", "REAL");
        await AddColumnIfMissingAsync(conn, "checks_header", "non_cash_money", "REAL");
        await AddColumnIfMissingAsync(conn, "checks_header", "sertificate_money", "REAL");
        await AddColumnIfMissingAsync(conn, "checks_header", "cash_money1", "REAL");
        await AddColumnIfMissingAsync(conn, "checks_header", "non_cash_money1", "REAL");
        await AddColumnIfMissingAsync(conn, "checks_header", "sertificate_money1", "REAL");
        await AddColumnIfMissingAsync(conn, "checks_header", "payment_by_sbp", "INTEGER");

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

// ★ Удаление чека: клиент передумал ДО оплаты. Точку входа используют
//   и кнопка «🗑», и стрелка «назад» при набивке (OnBackButtonPressed).
    private async void OnDeleteCheckClicked(object sender, EventArgs e)
        => await DeleteCurrentCheckAsync();

    // ★ Общий сценарий удаления чека: подтверждение → причина →
    //   запись its_deleted=1 → очистка экрана → возврат к списку ★
    private async Task DeleteCurrentCheckAsync()
    {
        if (!_isNewCheck || ProductsList.Count == 0) return;
        if (_checkClosed) return;       // закрытый (оплаченный) чек удалять нельзя
        if (_isPayProcessing) return;   // уже идёт оплата/удаление — двойной запуск исключён

        // ★ флаг ставим СРАЗУ, ещё до диалогов: пока сценарий живёт, «назад»
        //   заблокировано, повторные 🗑/стрелка не откроют второй сценарий
        _isPayProcessing = true;
        try
        {
            bool confirm = await DisplayAlert("Удаление чека",
                "Удалить текущий чек целиком?", "Да", "Нет");
            if (!confirm) return;

            // ★ модальный диалог: null = «Отмена» → чек НЕ удаляется
            string reason = await AskDeletionReasonAsync("Причина удаления чека");
            if (string.IsNullOrWhiteSpace(reason)) return;

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
            _promoBarcodes.Clear();       // ★ удалённый чек — без акционных ШК
            UpdatePromoBarcodesLabel();
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
        if (_isPreviewMode) return;   // в предпросмотре карту не меняем

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

        // ─── Стадия 3 (страховка): чек закрыт, а переход к списку не сработал —
        //     «СОЗДАТЬ ЧЕК» начинает новый чек на этой же странице ───
        if (_checkClosed)
        {
            await StartNewCheckOnSamePageAsync();
            return;
        }

        if (ProductsList.Count == 0)
        {
            await DisplayAlert("Внимание", "Чек пуст", "OK");
            return;
        }

        // ─── Стадия 1: НАБИВКА — переход в окно оплаты (или свайп) ───
        if (!_isPreviewMode)
        {
            await EnterPreviewModeAsync();
            return;
        }

        // ─── Стадия 2: ОКНО ОПЛАТЫ — «ОПЛАТИТЬ»: подтверждение и запись ───
        try
        {
            _isPayProcessing = true;

            // клиентская скидка 5% на сырые строки (состав чека не трогаем —
            // акции посчитает серверная обработка после выгрузки, как договорились)
            if (_isNewCheck)
            {
                await RecalculateCheckAsync();
                ApplyModeVisuals();   // вернуть «К ОПЛАТЕ» поверх перерисованных итогов
            }

            // ═══ ОПЛАТА: в подтверждении — та сумма, что кассир назвал покупателю ═══
            bool confirm = await DisplayAlert("Оплата",
                $"Сумма к оплате: {_previewTotal:F2} ₽. Подтвердить?", "Да", "Нет");
            if (!confirm)
                return; // остаёмся в окне оплаты — свайпом можно вернуться к набивке

            // ★ в comment — только комментарий кассира (причины удаления строк
            //   уже ушли в deleted_items в момент удаления)
            bool isWritten = await WriteCheckToDatabaseAsync(true, _payType, _cashierComment);

            if (isWritten)
            {
                await DisplayAlert("Успех",
                    _payType == 1 ? "Чек пробит. Оплата: QR-код" : "Чек пробит. Оплата: наличные",
                    "OK");

                // ★ чек закрыт и записан → уходим НАЗАД, к списку чеков, где уже
                //   виден пробитый чек. Состояние «закрыт» ставим до перехода:
                //   если навигация вдруг недоступна, кнопка покажет «СОЗДАТЬ ЧЕК»
                //   и по нажатию начнёт новый чек на этой же странице.
                _checkClosed = true;
                ApplyModeVisuals();
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

    // ★ «СОЗДАТЬ ЧЕК» на закрытом чеке: превращаем страницу в свежий новый чек ★
    private async Task StartNewCheckOnSamePageAsync()
    {
        _isPayProcessing = true;
        try
        {
            // новое «полотно» чека — как в конструкторе нового чека
            _checkClosed = false;
            _isPreviewMode = false;
            _previewItems = new List<ProductItem>();
            _previewTotal = 0m;
            _previewTotalFull = 0m;
            _checkGuid = Guid.NewGuid().ToString();   // новый guid: deleted_items нового чека привяжется к нему

            ProductsList.Clear();
            _clientCode = "";
            _clientHasDiscount = false;
            ClientEntry.Text = "";
            ClientEntry.IsVisible = true;
            ClientLabel.Text = "";
            _promoBarcodes.Clear();       // ★ новый чек — без акционных ШК (как Clear на десктопе)
            UpdatePromoBarcodesLabel();
            _cashierComment = "";
            CommentLabel.Text = "";
            CommentLabel.IsVisible = false;

            _checkStartTime = DateTime.Now;
            await GetNextDocumentNumberAsync();       // следующий номер документа

            SetPayType(0);          // по умолчанию — наличные
            ApplyModeVisuals();     // вернёт НАБИВКУ: синяя шапка, «ПЕРЕЙТИ К ОПЛАТЕ»
            UpdateTotalSum();       // ИТОГО = 0.00

            SearchProductEntry.Focus();
        }
        finally
        {
            _isPayProcessing = false;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  ★ ПРЕДПРОСМОТР ОКОНЧАТЕЛЬНОГО РАСЧЁТА — только чтение ★
    //  Считает ТОТ ЖЕ расчёт, что сделает серверная/десктопная обработка
    //  акций после оплаты: движок ProcessingOfActions на КОПИИ строк
    //  + скидка клиента 5% на строки без акций (правило десктопа).
    //  Сырые строки ProductsList не меняются, в БД ничего не пишется.
    //  Возвращает и ВСЕ сообщения движка (result.Messages — «Сработала
    //  акция…», «требуется ввод ШК подарка», «Кратность…», ошибки) —
    //  показать одним списком при входе в окно оплаты.
    // ══════════════════════════════════════════════════════════════════
    private async Task<(List<ProductItem> items, decimal total, decimal totalFull, List<string> messages)> BuildFinalPreviewAsync()
    {
        // сырые строки → рабочие строки движка (цены — только полные)
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
                return engine.Process(lines, _clientCode, new List<string>(_promoBarcodes));
            }
        });

        // 5% клиента — только на строки БЕЗ акций (правило десктопа)
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

        var items = result.Lines.Select(l => new ProductItem
        {
            Code = (int)l.TovarCode,
            Tovar = l.TovarName,
            Price = l.Price,
            Quantity = l.Quantity,
            PriceAtDiscount = l.PriceAtDiscount,
            Action = l.Action,
            Gift = l.Gift,
            PromoDescription = l.PromoDescription ?? ""
        }).ToList();

        decimal total = Math.Round(items.Sum(i => i.SumAtDiscount), 2, MidpointRounding.AwayFromZero);
        decimal totalFull = Math.Round(items.Sum(i => i.SumFull), 2, MidpointRounding.AwayFromZero);
        return (items, total, totalFull, result.Messages);
    }

    // ★ Вход в предпросмотр: свайп справа налево или кнопка «ПЕРЕЙТИ К ОПЛАТЕ» ★
    private async Task EnterPreviewModeAsync()
    {
        if (_isPreviewMode) return;
        if (!_isNewCheck) return;               // в старом чеке предпросмотра нет
        if (_isPayProcessing) return;

        if (ProductsList.Count == 0)
        {
            await DisplayAlert("Внимание", "Чек пуст", "OK");
            return;
        }

        _isPayProcessing = true;
        try
        {
            var (items, total, totalFull, promoMessages) = await BuildFinalPreviewAsync();

            SearchProductEntry.Unfocus();       // убрать клавиатуру, если была открыта
            ClientEntry.Unfocus();

            _previewItems = items;
            _previewTotal = total;
            _previewTotalFull = totalFull;
            _isPreviewMode = true;
            ApplyModeVisuals();

            // ★ ВСЕ сообщения от обработки акций — одним списком (замена каскада
            //   MessageBox десктопа). Показываем ПОСЛЕ переключения экрана: окно
            //   оплаты уже за диалогом, и кассир сразу видит, к чему относятся
            //   сообщения. Пустой список — диалога нет, расчёт прошёл тихо.
            if (promoMessages.Count > 0)
                await DisplayAlert("Акции", string.Join("\n", promoMessages), "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка расчёта", ex.Message, "OK");
        }
        finally
        {
            _isPayProcessing = false;
        }
    }

    // ★ Возврат к набивке: свайп слева направо ★
    private void ExitPreviewMode()
    {
        if (!_isPreviewMode) return;
        if (_checkClosed) return;   // чек закрыт: возврата к набивке нет — только «СОЗДАТЬ ЧЕК»

        _isPreviewMode = false;

        // состав чека в окне оплаты не менялся — показываем в ИТОГО уже
        // посчитанную сумму с акциями, без повторного прогона движка
        _compositionPromoTotal = _previewTotal;
        _compositionPromoTotalValid = true;

        ApplyModeVisuals();
        UpdateTotalSum(keepPromoTotal: true);
    }

    private async void OnSwipeToPaymentPreview(object sender, SwipedEventArgs e)
        => await EnterPreviewModeAsync();

    private void OnSwipeBackToComposition(object sender, SwipedEventArgs e)
        => ExitPreviewMode();

    // ══════════════════════════════════════════════════════════════════
    //  ★ СТРЕЛКА «НАЗАД» (левый верхний угол) И СИСТЕМНАЯ КНОПКА «НАЗАД» ★
    //  В окне оплаты обе должны работать как свайп слева направо —
    //  возвращать к НАБИВКЕ текущего чека, а не уводить к списку чеков
    //  (иначе открытый чек терялся: NavigationPage просто закрывал страницу).
    //  NavigationPage на Android направляет и стрелку навбара, и системную
    //  «назад» в OnBackButtonPressed; true = переход (pop) отменён.
    //  Пока идёт расчёт/запись оплаты (_isPayProcessing) — «назад» блокируем.
    //  При НАБИВКЕ нового чека: чек пуст — обычный уход к списку (терять нечего);
    //  есть товары — работаем как кнопка «🗑», чтобы чек не пропадал молча.
    //  На закрытом чеке и в просмотре старого — стандартный уход к списку.
    // ══════════════════════════════════════════════════════════════════
    protected override bool OnBackButtonPressed()
    {
        if (_isPayProcessing) return true;   // критическая секция — страницу не закрываем

        // окно оплаты открытого чека → возврат к набивке, как свайп слева направо
        if (_isNewCheck && _isPreviewMode && !_checkClosed)
        {
            ExitPreviewMode();
            return true;   // переход к списку чеков отменён — чек остался открытым
        }

        // ★ НАБИВКА нового чека с ТОВАРАМИ: то же, что кнопка «🗑» —
        //   подтверждение → причина → запись удалённого чека (its_deleted=1)
        //   → возврат к списку. Молча терять набранный чек нельзя.
        if (_isNewCheck && !_checkClosed && ProductsList.Count > 0)
        {
            _ = DeleteCurrentCheckAsync();    // сценарий кнопки «🗑» — диалоги ведут дальше
            return true;                      // стандартный уход к списку отменён
        }

        // пустой новый чек, закрытый чек, просмотр старого → стандартный переход к списку
        return base.OnBackButtonPressed();
    }

    // ══════════════════════════════════════════════════════════════════
    //  ★ ВВОД АКЦИОННОГО ШТРИХКОДА (кнопка «★ ШК АКЦИИ» / свайп вверх или вниз) ★
    // ══════════════════════════════════════════════════════════════════
    private async void OnPromoBarcodeClicked(object sender, EventArgs e)
        => await OpenPromoBarcodeInputAsync();

    private async void OnSwipePromoBarcode(object sender, SwipedEventArgs e)
        => await OpenPromoBarcodeInputAsync();

    private async Task OpenPromoBarcodeInputAsync()
    {
        // только новый чек и только при набивке: в окне оплаты состав не меняется
        if (!_isNewCheck || _isPreviewMode || _checkClosed || _isPayProcessing) return;

        string input;
        try
        {
            input = await DisplayPromptAsync("Акционный штрихкод",
                "Отсканируйте или введите штрихкод акции",
                maxLength: 50) ?? "";
        }
        finally
        {
            SearchProductEntry.Focus();   // фокус обратно в поиск — продолжаем сканировать (как на десктопе)
        }

        input = input.Trim();
        if (input.Length == 0) return;

        // ★ дубль — сообщаем и второй раз не добавляем
        if (_promoBarcodes.Contains(input))
        {
            await DisplayAlert("Акционный штрихкод",
                "Этот штрихкод уже есть в списке акционных штрихкодов", "OK");
            return;
        }

        // ★ паритет с десктопной CheckAction: акция с таким ШК должна быть активна сегодня
        if (!await CheckActionBarcodeExistsAsync(input))
        {
            await DisplayAlert("Проверка ввода", "Акция с таким штрихкодом не найдена", "OK");
            return;
        }

        _promoBarcodes.Add(input);
        UpdatePromoBarcodesLabel();

        // ШК-акции приоритетны: пересчитываем «сумму с учётом акций» (второе число ИТОГО)
        UpdateTotalSum();
    }

    // ★ активна ли сегодня акция с таким ШК (action_header.barcode) —
    //   локальная SQLite вместо десктопного запроса к Postgres ★
    private async Task<bool> CheckActionBarcodeExistsAsync(string barcode)
    {
        try
        {
            using (var conn = MainStaticClass.GetLocalSQLiteConnection())
            {
                await conn.OpenAsync();
                using (var cmd = new SqliteCommand(
                           "SELECT date_started, date_end FROM action_header WHERE TRIM(barcode) = @bc", conn))
                {
                    cmd.Parameters.AddWithValue("@bc", barcode);
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        DateTime today = DateTime.Today;
                        while (await r.ReadAsync())
                        {
                            string rawStart = r.IsDBNull(0) ? "" : r.GetValue(0).ToString().Trim();
                            string rawEnd = r.IsDBNull(1) ? "" : r.GetValue(1).ToString().Trim();

                            DateTime? started = ParseActionDate(rawStart);
                            DateTime? end = ParseActionDate(rawEnd);

                            if (started.HasValue && end.HasValue)
                            {
                                if (started.Value.Date <= today && today <= end.Value.Date) return true;
                            }
                            else
                            {
                                // формат дат нестандартный — строковое сравнение, как на десктопе
                                string t = today.ToString("yyyy-MM-dd");
                                if (string.CompareOrdinal(rawStart, t) <= 0 &&
                                    string.CompareOrdinal(t, rawEnd) <= 0) return true;
                            }
                        }
                    }
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Проверка акционного ШК: {ex.Message}");
            return false;
        }
    }

    // те же форматы дат, что и в движке акций (ProcessingOfActions)
    private static DateTime? ParseActionDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        if (s.Length > 10) s = s.Substring(0, 10);
        if (DateTime.TryParseExact(s,
                new[] { "yyyy-MM-dd", "dd.MM.yyyy", "dd-MM-yyyy", "yyyy.MM.dd" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out DateTime d))
            return d;
        return null;
    }

    // ★ строка списка ШК + счётчик на кнопке ★
    private void UpdatePromoBarcodesLabel()
    {
        if (_promoBarcodes.Count == 0)
        {
            PromoBarcodesLabel.IsVisible = false;
            PromoBarcodesLabel.Text = "";
            PromoBarcodeBtn.Text = "★ ШК АКЦИИ";
            return;
        }

        PromoBarcodesLabel.IsVisible = true;
        PromoBarcodesLabel.Text = "★ ШК акции: " + string.Join(", ", _promoBarcodes);
        PromoBarcodeBtn.Text = $"★ ШК АКЦИИ ({_promoBarcodes.Count})";
    }

    // ★ Переключение экрана между режимами «НАБИВКА» ↔ «ОПЛАТА» ★
    private void ApplyModeVisuals()
    {
        bool p = _isPreviewMode;

        // верхняя панель: синяя «Касса №…» ↔ зелёная «ОПЛАТА»
        TopBar.BackgroundColor = p ? Color.FromArgb("#2E7D32") : Color.FromArgb("#007ACC");
        NumCashLabel.IsVisible = !p;
        DateTimeLabel.IsVisible = !p;
        UserLabel.IsVisible = !p;
        PayModeLabel.IsVisible = p;

        // строка поиска/сканера/карты клиента — только при набивке
        SearchRow.IsVisible = !p;

        // табличная часть: сырые строки ↔ окончательный расчёт (из памяти)
        // (явное приведение — чтобы тернарник собрался при любом LangVersion)
        ProductsCollectionView.ItemsSource = p
            ? (System.Collections.IEnumerable)_previewItems
            : ProductsList;

        // итоги: ИТОГО «без акций / с акциями» при набивке ↔ К ОПЛАТЕ в окне оплаты
        // ↔ ОПЛАЧЕНО на закрытом чеке
        TotalCaptionLabel.Text = _checkClosed ? "ОПЛАЧЕНО:" : (p ? "К ОПЛАТЕ:" : "ИТОГО:");
        if (p)
            TotalSumLabel.Text = $"{_previewTotal:F2} ₽";
        else if (_checkClosed)
            TotalSumLabel.Text = $"{Math.Round(ProductsList.Sum(x => x.SumAtDiscount), 2):F2} ₽";
        else
            RenderCompositionTotal();   // «X / Y ₽» — с клиентской скидкой / с учётом акций

        decimal disc = Math.Round(_previewTotalFull - _previewTotal, 2, MidpointRounding.AwayFromZero);
        DiscountInfoLabel.IsVisible = p && disc > 0;
        DiscountInfoLabel.Text = $"Без скидки: {_previewTotalFull:F2} ₽ · Ваша скидка: {disc:F2} ₽";

        // ★ нижняя кнопка — три стадии:
        //   НАБИВКА → «ПЕРЕЙТИ К ОПЛАТЕ», окно оплаты → «ОПЛАТИТЬ»,
        //   и только ПОСЛЕ закрытия чека → «СОЗДАТЬ ЧЕК» (начинает новый чек)
        if (_checkClosed)
        {
            PayButton.Text = "СОЗДАТЬ ЧЕК";
            PayButton.BackgroundColor = Color.FromArgb("#2196F3");
        }
        else if (p)
        {
            PayButton.Text = "ОПЛАТИТЬ";
            PayButton.BackgroundColor = Color.FromArgb("#2E7D32");
        }
        else
        {
            PayButton.Text = "ПЕРЕЙТИ К ОПЛАТЕ →";
            PayButton.BackgroundColor = Color.FromArgb("#2196F3");
        }
        DeleteCheckButton.IsVisible = !p;

        // ★ кнопка акционного ШК — только при набивке нового чека;
        //   сам список введённых ШК (строка под кнопками) остаётся виден в окне оплаты
        PromoBarcodeBtn.IsVisible = _isNewCheck && !p && !_checkClosed;

        // подсказка кассиру
        ModeHintLabel.IsVisible = _isNewCheck;
        ModeHintLabel.Text = _checkClosed
            ? "✓ Чек закрыт и записан — возвращаемся к списку чеков"
            : (p
                ? "← стрелка «назад» или свайп слева направо — вернуться к набивке"
                : "↓↑ свайп вверх/вниз или «★ ШК» — акционный штрихкод · ← окончательная цена · итог: без акций / с акциями");

        OnPropertyChanged(nameof(IsEditable));   // +/−/✕ в строках: включены только при набивке
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
    
    // ★ Пересчёт чека: клиентская скидка 5% + (опционально) акции + перерисовка списка ★
    // Всегда считает от ПОЛНЫХ цен (PriceAtDiscount = Price на входе), поэтому
    // любое количество повторных пересчётов не задваивает скидки.
    private async Task RecalculateCheckAsync(bool showMessages = true)
    {
        if (ApplyActionsDuringComposition)
        {
            // ─── СТАРОЕ поведение: акции считаются на планшете при наборе ───
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
                    return engine.Process(lines, _clientCode, new List<string>(_promoBarcodes));
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
        else
        {
            // ─── НОВОЕ поведение (паритет с десктопом): СОСТАВ ЧЕКА НЕ ТРОГАЕМ ───
            // Только перерасчёт цен со скидкой клиента 5% (состав строки не меняется).
            // Никаких подарков за 1 ₽ и разбиения строк при наборе.
            var snapshot = ProductsList.ToList();
            ProductsList.Clear();
            foreach (var p in snapshot)
            {
                p.PriceAtDiscount = _clientHasDiscount
                    ? Math.Round(p.Price - p.Price * 0.05m, 2, MidpointRounding.AwayFromZero)
                    : p.Price;
                p.Action = 0;
                p.Gift = 0;
                p.PromoDescription = "";
                ProductsList.Add(p);
            }

            UpdateTotalSum();
        }
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

                    // ★ РАЗБИВКА СУММЫ ПО ВИДАМ ОПЛАТЫ (паритет с десктопом):
                    //   наличные (payType=0) → вся сумма в cash_money;
                    //   QR = СБП (payType=1) → вся сумма в non_cash_money — сервер читает
                    //   её как безналичную (Sum_terminal), плюс ставится payment_by_sbp=1,
                    //   как в SendDataOnSalesPortions. Сертификатов на планшете нет —
                    //   sertificate_money всегда 0.
                    decimal cashPart = payType == 0 ? totalCash : 0m;
                    decimal nonCashPart = payType == 1 ? totalCash : 0m;

                    string insertHeader = @"INSERT INTO checks_header 
                    (document_number, date_time_start, date_time_write, client, cash_desk_number, comment, cash, remainder, discount, autor, its_deleted, check_type, guid, is_sent, pay_type, cash_money, non_cash_money, sertificate_money, cash_money1, non_cash_money1, sertificate_money1, payment_by_sbp) 
                    VALUES (@doc, @start, @write, @client, @desk, @comment, @cash, @rem, @disc, @autor, @del, @type, @guid, 0, @paytype, @cashmoney, @noncash, @sert, @cashmoney1, @noncash1, @sert1, @sbp)";

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

                        // ★ суммы по видам оплаты → безналичная попадает в non_cash_money
                        //   (сервер отдаёт её как Sum_terminal — как при оплате картой на десктопе)
                        cmd.Parameters.AddWithValue("@cashmoney", cashPart);
                        cmd.Parameters.AddWithValue("@noncash", nonCashPart);
                        cmd.Parameters.AddWithValue("@sert", 0m);
                        // ★ «первые» суммы на десктопе заполняются только при
                        //   SystemTaxation 3/5 (get_cash_on_type_payment_3_new),
                        //   в остальных случаях там нули — повторяем это
                        cmd.Parameters.AddWithValue("@cashmoney1", 0m);
                        cmd.Parameters.AddWithValue("@noncash1", 0m);
                        cmd.Parameters.AddWithValue("@sert1", 0m);
                        // ★ QR на планшете = СБП (подтверждено кассирами): для QR-оплаты
                        //   ставим флаг payment_by_sbp=1 (как на десктопе), сумма при этом
                        //   по-прежнему уходит в non_cash_money → Sum_terminal
                        cmd.Parameters.AddWithValue("@sbp", payType == 1 ? 1 : 0);

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
