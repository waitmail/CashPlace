using Microsoft.Data.Sqlite;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace CashPlace;

public partial class CheckPage : ContentPage
{
    public ObservableCollection<ProductItem> ProductsList { get; set; } = new();
    
    private bool _isNewCheck = true;
    private string _existingCheckDate = "";
    private long _numdoc = 0;
    private DateTime _checkStartTime; 
    
    // Добавляем публичное свойство, чтобы XAML мог управлять видимостью кнопок
    public bool IsNewCheck => _isNewCheck;

    // Конструктор для НОВОГО чека
    public CheckPage()
    {
        InitializeComponent();
        BindingContext = this;
        
        //NumCashLabel.Text = $"Касса № {MainStaticClass.CashDeskNumber}";
        NumCashLabel.Text = $"Касса № "+MainStaticClass.CashDeskNumber.ToString();
        DateTimeLabel.Text = DateTime.Now.ToString("dd.MM HH:mm");
        //UserLabel.Text = MainStaticClass.Cash_Operator;
        UserLabel.Text = "Тестовый опер";
        
        // Генерируем номер документа
        //_numdoc = DateTime.Now.Ticks % 1000000000; 
        
        _checkStartTime = DateTime.Now; 
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

    // ★ НОВЫЙ МЕТОД: ГЕНЕРАЦИЯ ПОРЯДКОВОГО НОМЕРА ★
    private async Task GetNextDocumentNumberAsync()
    {
        try
        {
            using (var conn = MainStaticClass.GetLocalSQLiteConnection())
            {
                await conn.OpenAsync();
                // Находим самый большой номер документа
                string query = "SELECT MAX(document_number) FROM checks_header";
                using (var cmd = new SqliteCommand(query, conn))
                {
                    var result = await cmd.ExecuteScalarAsync();
                    
                    // Если база не пустая и результат не NULL, берем число и прибавляем 1
                    if (result != null && result != DBNull.Value)
                    {
                        _numdoc = Convert.ToInt64(result) + 1;
                    }
                    else
                    {
                        // Если чеков еще нет, начинаем с 1
                        _numdoc = 1;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Если ошибка, выдаем случайный номер, чтобы чек не завис
            Debug.WriteLine($"Ошибка получения номера чека: {ex.Message}");
            _numdoc = new Random().Next(1, 9999);
        }
    }
    
    // // Срабатывает при появлении страницы на экране
    // protected override void OnAppearing()
    // {
    //     base.OnAppearing();
    //     
    //     // Если это новый чек, сразу ставим фокус в поле ввода для сканера
    //     if (_isNewCheck)
    //     {
    //         SearchProductEntry.Focus();
    //     }
    // }

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
        
        //NumCashLabel.Text = $"Касса № {MainStaticClass.CashDeskNumber}";
        NumCashLabel.Text = $"Касса № "+MainStaticClass.CashDeskNumber.ToString();
        DateTimeLabel.Text = dateTimeWrite.ToString("dd.MM HH:mm");
        
        if (long.TryParse(documentNumber, out long docNum))
        {
            _numdoc = docNum;
            LoadExistingCheck();
        }
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

                // ★ ЛОГИКА КАК В POSTGRES: Если код длиннее 6 символов — ищем в таблице barcode
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
                // ★ ИНАЧЕ ИЩЕМ ПРЯМО В ТАБЛИЦЕ tovar ПО КОДУ
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
                            PriceAtDiscount = Convert.ToDecimal(reader.GetValue(2)),
                            // ★ ВАЖНО: Индексы колонок изменились! fractional теперь на позиции 6 (была 3)
                            IsFractional = Convert.ToInt32(reader.GetValue(6)) == 1
                            
                            // Если в модели ProductItem есть поля IsMarked, IsCertificate и т.д., 
                            // их тоже можно здесь заполнить:
                            // IsMarked = Convert.ToInt32(reader.GetValue(4)) == 1,
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

            UpdateTotalSum();
            ProductsCollectionView.ScrollTo(ProductsList.Count - 1);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }
    }

    private void OnIncreaseQuantity(object sender, EventArgs e)
    {
        // ✅ Защита от изменения в старом чеке
        if (!_isNewCheck) return; 

        if (sender is Button btn && btn.BindingContext is ProductItem product)
        {
            if (!product.IsMarked && !product.IsFractional)
            {
                product.Quantity++;
                UpdateTotalSum();
            }
            else
            {
                ShowQuantityDialog(product);
            }
        }
    }

    private void OnDecreaseQuantity(object sender, EventArgs e)
    {
        // ✅ Защита от изменения в старом чеке
        if (!_isNewCheck) return; 

        if (sender is Button btn && btn.BindingContext is ProductItem product)
        {
            if (product.Quantity > 1)
            {
                product.Quantity--;
                UpdateTotalSum();
            }
        }
    }

    private async void ShowQuantityDialog(ProductItem product)
    {
        string result = await DisplayPromptAsync("Количество", product.Tovar, initialValue: product.Quantity.ToString());
        if (decimal.TryParse(result, out decimal newQty) && newQty > 0)
        {
            product.Quantity = newQty;
            UpdateTotalSum();
        }
    }

    private void UpdateTotalSum()
    {
        decimal total = ProductsList.Sum(p => p.SumAtDiscount);
        TotalSumLabel.Text = $"{total:F2} ₽";
    }

    private async void PayButton_Clicked(object sender, EventArgs e)
    {
        if (ProductsList.Count == 0)
        {
            await DisplayAlert("Внимание", "Чек пуст", "OK");
            return;
        }

        bool confirm = await DisplayAlert("Оплата", $"Сумма к оплате: {TotalSumLabel.Text}. Подтвердить?", "Да", "Нет");
        if (!confirm) return;

        bool isWritten = await WriteCheckToDatabaseAsync(true);
        if (isWritten)
        {
            await DisplayAlert("Успех", "Чек пробит и записан в базу", "OK");
            await Navigation.PopAsync(); // Возврат к списку чеков
        }
    }
    
    private void PlayErrorSound()
    {
        try
        {
            Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(300));

// #if ANDROID
//             var context = Android.App.Application.Context;
//             var defaultSoundUri = Android.Provider.Settings.System.DefaultNotificationUri;
//         
//             var ringtone = Android.Media.RingtoneManager.GetRingtone(context, defaultSoundUri);
//             ringtone?.Play();
// #endif
#if ANDROID
            var toneGen = new Android.Media.ToneGenerator(Android.Media.Stream.Notification, 100);
// Варианты для ошибки (от более мягкого к более резкому):
// toneGen.StartTone(Android.Media.Tone.CdmaAlertNetworkLite, 300);  // Мягкий
            toneGen.StartTone(Android.Media.Tone.SupError, 300);              // Стандартная ошибка
// toneGen.StartTone(Android.Media.Tone.CdmaAlertCallGuard, 300);    // Тревожный
// toneGen.StartTone(Android.Media.Tone.PropBeep, 200);                 // Короткий двойной писк

            toneGen.Release();
#endif
        }
        catch { }
    }
    
    
    //     private async Task<bool> WriteCheckToDatabaseAsync(bool isPaid)
    // {
    //     try
    //     {
    //         // Генерируем GUID один раз для всего чека
    //         string checkGuid = Guid.NewGuid().ToString();
    //
    //         using (var conn = MainStaticClass.GetLocalSQLiteConnection())
    //         {
    //             await conn.OpenAsync();
    //             using (var tran = await conn.BeginTransactionAsync())
    //             {
    //                 // Удаляем старые записи, если перезаписываем
    //                 using (var delCmd = new SqliteCommand("DELETE FROM checks_table WHERE document_number=@doc; DELETE FROM checks_header WHERE document_number=@doc;", conn, (SqliteTransaction)tran))
    //                 {
    //                     delCmd.Parameters.AddWithValue("@doc", _numdoc);
    //                     await delCmd.ExecuteNonQueryAsync();
    //                 }
    //
    //                 // Вставляем шапку
    //                 string insertHeader = @"INSERT INTO checks_header 
    //                     (document_number, date_time_start, date_time_write, client, cash_desk_number, comment, cash, remainder, discount, autor, its_deleted, check_type, guid, is_sent) 
    //                     VALUES (@doc, @start, @write, @client, @desk, @comment, @cash, @rem, @disc, @autor, @del, @type, @guid, 0)";
    //                 
    //                 using (var cmd = new SqliteCommand(insertHeader, conn, (SqliteTransaction)tran))
    //                 {
    //                     cmd.Parameters.AddWithValue("@doc", _numdoc);
    //                     cmd.Parameters.AddWithValue("@start", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    //                     cmd.Parameters.AddWithValue("@write", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    //                     cmd.Parameters.AddWithValue("@client", ""); 
    //                     cmd.Parameters.AddWithValue("@comment", ""); 
    //                     cmd.Parameters.AddWithValue("@cash", ProductsList.Sum(p => p.SumAtDiscount));
    //                     cmd.Parameters.AddWithValue("@desk", "11");
    //                     cmd.Parameters.AddWithValue("@rem", 0); 
    //                     cmd.Parameters.AddWithValue("@disc", 0); 
    //                     cmd.Parameters.AddWithValue("@autor", "910214609785");
    //                     cmd.Parameters.AddWithValue("@del", isPaid ? 0 : 2);
    //                     cmd.Parameters.AddWithValue("@type", 0); 
    //                     cmd.Parameters.AddWithValue("@guid", checkGuid); // ✅ Передаем сгенерированный GUID
    //                     
    //                     await cmd.ExecuteNonQueryAsync();
    //                 }
    //
    //                 // Вставляем товары
    //                 foreach (var p in ProductsList)
    //                 {
    //                     string insertItem = @"INSERT INTO checks_table 
    //                         (document_number, tovar_code, name, quantity, price, price_at_a_discount, sum, sum_at_a_discount, guid) 
    //                         VALUES (@doc, @code, @name, @qty, @price, @price_disc, @sum, @sum_disc, @guid)";
    //                     
    //                     using (var cmd = new SqliteCommand(insertItem, conn, (SqliteTransaction)tran))
    //                     {
    //                         cmd.Parameters.AddWithValue("@doc", _numdoc);
    //                         cmd.Parameters.AddWithValue("@code", p.Code);
    //                         cmd.Parameters.AddWithValue("@name", p.Tovar);
    //                         cmd.Parameters.AddWithValue("@qty", p.Quantity);
    //                         cmd.Parameters.AddWithValue("@price", p.PriceAtDiscount);
    //                         cmd.Parameters.AddWithValue("@price_disc", p.PriceAtDiscount);
    //                         cmd.Parameters.AddWithValue("@sum", p.SumAtDiscount);
    //                         cmd.Parameters.AddWithValue("@sum_disc", p.SumAtDiscount);
    //                         cmd.Parameters.AddWithValue("@guid", checkGuid); // ✅ Передаем тот же самый GUID
    //                         
    //                         await cmd.ExecuteNonQueryAsync();
    //                     }
    //                 }
    //
    //                 await tran.CommitAsync();
    //                 return true;
    //             }
    //         }
    //     }
    //     catch (Exception ex)
    //     {
    //         await DisplayAlert("Ошибка БД", ex.Message, "OK");
    //         return false;
    //     }
    // }
    
        private async Task<bool> WriteCheckToDatabaseAsync(bool isPaid)
    {
        try
        {
            // Генерируем GUID один раз для всего чека
            string checkGuid = Guid.NewGuid().ToString();

            using (var conn = MainStaticClass.GetLocalSQLiteConnection())
            {
                await conn.OpenAsync();
                using (var tran = await conn.BeginTransactionAsync())
                {
                    // Удаляем старые записи, если перезаписываем
                    using (var delCmd = new SqliteCommand("DELETE FROM checks_table WHERE document_number=@doc; DELETE FROM checks_header WHERE document_number=@doc;", conn, (SqliteTransaction)tran))
                    {
                        delCmd.Parameters.AddWithValue("@doc", _numdoc);
                        await delCmd.ExecuteNonQueryAsync();
                    }

                    // ★ СЧИТАЕМ ИТОГО СО СКИДКОЙ И ОКРУГЛЯЕМ ★
                    decimal totalCash = ProductsList.Sum(p => p.SumAtDiscount);
                    totalCash = Math.Round(totalCash, 2);

                    // Вставляем шапку
                    string insertHeader = @"INSERT INTO checks_header 
                        (document_number, date_time_start, date_time_write, client, cash_desk_number, comment, cash, remainder, discount, autor, its_deleted, check_type, guid, is_sent) 
                        VALUES (@doc, @start, @write, @client, @desk, @comment, @cash, @rem, @disc, @autor, @del, @type, @guid, 0)";
                    
                    using (var cmd = new SqliteCommand(insertHeader, conn, (SqliteTransaction)tran))
                    {
                        cmd.Parameters.AddWithValue("@doc", _numdoc);
                        //cmd.Parameters.AddWithValue("@start", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        // ★ ИСПОЛЬЗУЕМ ВРЕМЯ СТАТА ДЛЯ @start
                        cmd.Parameters.AddWithValue("@start", _checkStartTime.ToString("yyyy-MM-dd HH:mm:ss"));
                        cmd.Parameters.AddWithValue("@write", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        cmd.Parameters.AddWithValue("@client", ""); 
                        cmd.Parameters.AddWithValue("@comment", ""); 
                        cmd.Parameters.AddWithValue("@cash", totalCash); // ✅ Передаем посчитанную сумму
                        cmd.Parameters.AddWithValue("@desk", MainStaticClass.CashDeskNumber.ToString());
                        cmd.Parameters.AddWithValue("@rem", 0); 
                        cmd.Parameters.AddWithValue("@disc", 0); 
                        cmd.Parameters.AddWithValue("@autor", "111111222222");//"910214609785"
                        cmd.Parameters.AddWithValue("@del", isPaid ? 0 : 2);
                        cmd.Parameters.AddWithValue("@type", 0); 
                        cmd.Parameters.AddWithValue("@guid", checkGuid);
                        
                        await cmd.ExecuteNonQueryAsync();
                    }

                    // Вставляем товары
                    foreach (var p in ProductsList)
                    {
                        string insertItem = @"INSERT INTO checks_table 
                            (document_number, tovar_code, name, quantity, price, price_at_a_discount, sum, sum_at_a_discount, guid) 
                            VALUES (@doc, @code, @name, @qty, @price, @price_disc, @sum, @sum_disc, @guid)";
                        
                        using (var cmd = new SqliteCommand(insertItem, conn, (SqliteTransaction)tran))
                        {
                            cmd.Parameters.AddWithValue("@doc", _numdoc);
                            cmd.Parameters.AddWithValue("@code", p.Code);
                            cmd.Parameters.AddWithValue("@name", p.Tovar);
                            cmd.Parameters.AddWithValue("@qty", p.Quantity);
                            cmd.Parameters.AddWithValue("@price", p.PriceAtDiscount);
                            cmd.Parameters.AddWithValue("@price_disc", p.PriceAtDiscount);
                            cmd.Parameters.AddWithValue("@sum", p.SumAtDiscount);
                            cmd.Parameters.AddWithValue("@sum_disc", p.SumAtDiscount);
                            cmd.Parameters.AddWithValue("@guid", checkGuid);
                            
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
            // Переключаемся на главный поток, чтобы изменить UI
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // 1. Вставляем считанный код в поле ввода
                SearchProductEntry.Text = result;
                
                // 2. Программно вызываем событие "Enter" (Completed)
                // Это запустит стандартный поиск товара, как будто код ввел физический сканер
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
                // Мы можем не выбирать sum_at_a_discount из БД, так как модель посчитает её сама
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
                                // Как только мы задаем PriceAtDiscount, SumAtDiscount посчитается сам
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