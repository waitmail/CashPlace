using Microsoft.Data.Sqlite;
using CashPlace.Services.Actions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace CashPlace
{
    // ── модели строк для трёх таблиц ──
    // (верхний уровень namespace: MAUI XAML не разрешает вложенные типы в x:DataType)

    /// <summary>Строка «Товары в чеке» (входная набивка, паритет dt1).</summary>
    public class VerifyProductItem
    {
        public long Code { get; set; }
        public string Name { get; set; } = "";
        public decimal Quantity { get; set; } = 1;
        public decimal Price { get; set; }              // ПОЛНАЯ цена — в движок уходит она
        public bool ClientApplied { get; set; }         // показывать −5% клиента (как GetPriceWithDiscount)

        public string CodeDisplay => Code.ToString();
        public string QtyDisplay => Quantity.ToString("0.###", CultureInfo.InvariantCulture);
        // отображаемая цена: с клиентской скидкой 5% (паритет десктопного окна) или полная
        private decimal DisplayPrice => ClientApplied
            ? Math.Round(Price - Price * 0.05m, 2, MidpointRounding.AwayFromZero)
            : Price;
        public string PriceDisplay => DisplayPrice.ToString("F2", CultureInfo.InvariantCulture) + " ₽";
        public string SumDisplay => Math.Round(DisplayPrice * Quantity, 2, MidpointRounding.AwayFromZero)
                                    .ToString("F2", CultureInfo.InvariantCulture) + " ₽";
    }

    /// <summary>Строка «Выполненные товары» (результат движка, паритет dt2).</summary>
    public class VerifyExecuteItem
    {
        public long Code { get; set; }
        public string Name { get; set; } = "";
        public decimal Quantity { get; set; }
        public decimal PriceAtDiscount { get; set; }
        public decimal SumAtDiscount { get; set; }
        public int Action2 { get; set; }                // № акционного документа, занявшего позицию
        public string PromoDescription { get; set; } = "";

        public string CodeDisplay => Code.ToString();
        public string QtyDisplay => Quantity.ToString("0.###", CultureInfo.InvariantCulture);
        public string PriceDisplay => PriceAtDiscount.ToString("F2", CultureInfo.InvariantCulture) + " ₽";
        public string SumDisplay => SumAtDiscount.ToString("F2", CultureInfo.InvariantCulture) + " ₽";
        public string Action2Display => Action2 == 0 ? "" : Action2.ToString();
        public bool IsAction => Action2 != 0;           // жёлтая подсветка строки
    }

    /// <summary>Строка «Участие в механических акциях» (паритет dt3).</summary>
    public class VerifyParticipationItem
    {
        public int NumDoc { get; set; }
        public int Tip { get; set; }
        public string Comment { get; set; } = "";
        public int ExecutionOrder { get; set; }

        public string NumDocDisplay => NumDoc.ToString();
        public string TipDisplay => Tip.ToString();
        public string ExecutionOrderDisplay => ExecutionOrder.ToString();
    }

    // ══════════════════════════════════════════════════════════════════
    //  ★ ПРОВЕРКА АКЦИЙ — планшетный аналог десктопного окна VerifyActions ★
    //  «Черновой чек»: кассир сканирует товары (или вводит код), вводит карту
    //  клиента и акционные ШК, движок ProcessingOfActions считает акции
    //  НА КОПИИ строк — НИЧЕГО в БД не пишется и на сервер не выгружается.
    //  Паритет десктопа:
    //   - ШК длиной > 4 ищется в action_header.barcode, ≤ 4 — в promo_code
    //     (CheckAction) → добавляется в список акционных ШК;
    //   - товар: длина > 6 — таблица barcode, иначе код товара
    //     (its_deleted=0, retail_price<>0; для кода ещё its_certificate=0);
    //   - клиент: 10 цифр с «9» → поиск по phone, иначе по code → 5% на
    //     строки без акций;
    //   - пересчёт после каждого изменения, итоговая сумма — по результату
    //     движка (как Calculate по dt2);
    //   - третья таблица — участие последнего введённого товара/ШК в акциях
    //     (GetParticipationMechanicalAction).
    // ══════════════════════════════════════════════════════════════════
    public partial class VerifyActionsPage : ContentPage
    {
        // ── состояние экрана ──
        private readonly ObservableCollection<VerifyProductItem> _products = new();
        private readonly ObservableCollection<VerifyExecuteItem> _executeItems = new();
        private readonly ObservableCollection<VerifyParticipationItem> _participationItems = new();

        private readonly List<string> _actionBarcodeList = new();   // паритет action_barcode_list
        private string _clientCode = "";
        private bool _clientHasDiscount;

        private int _recalcGeneration;          // устаревшие пересчёты не показываем
        private bool _recalcBusy;               // защита от параллельного пересчёта
        private bool _recalcPending;            // скан пришёл во время пересчёта → пересчитать ещё раз
        private bool _promoCodeColumnChecked;   // есть ли в локальной БД колонка action_header.promo_code
        private bool _promoCodeColumnExists;

        public VerifyActionsPage()
        {
            InitializeComponent();

            TovarCollectionView.ItemsSource = _products;
            TovarExecuteCollectionView.ItemsSource = _executeItems;
            ParticipationCollectionView.ItemsSource = _participationItems;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            Dispatcher.Dispatch(() => { try { BarcodeEntry.Focus(); } catch { /* не критично */ } });
        }

        // ══════════════════════════════════════════════════════════════
        //  ВВОД ШК / КОДА ТОВАРА (паритет TxtB_input_code_or_barcode_KeyDown
        //  → FindBarcodeOrCodeInTovar)
        // ══════════════════════════════════════════════════════════════
        private async void OnBarcodeCompleted(object sender, EventArgs e)
        {
            string input = (BarcodeEntry.Text ?? "").Trim().Replace("\r\n", "");
            BarcodeEntry.Text = "";
            if (input.Length == 0) return;

            try
            {
                // 1. Акционный ШК? (паритет CheckAction: >4 симв. — barcode, ≤4 — promo_code)
                if (await IsActionBarcodeAsync(input))
                {
                    if (_actionBarcodeList.Contains(input))
                    {
                        await DisplayAlert("Акционный штрихкод",
                            "Этот штрихкод уже есть в списке акционных штрихкодов", "OK");
                        return;
                    }
                    _actionBarcodeList.Add(input);
                    await LoadParticipationAsync(input);   // для ШК список участия будет пуст (паритет)
                    await RecalcActionsAsync();
                    return;
                }

                // 2. Обычный товар: длина > 6 — по таблице barcode, иначе — код товара
                VerifyProductItem found = await FindProductAsync(input);
                if (found == null)
                {
                    await DisplayAlert("Проверка акций", "Товар не найден", "OK");
                    return;
                }

                // 3. Уже есть в наборе → +1, иначе новая строка (паритет десктопа)
                var existing = _products.FirstOrDefault(p => p.Code == found.Code);
                if (existing != null)
                    existing.Quantity += 1;
                else
                {
                    found.ClientApplied = _clientHasDiscount;
                    _products.Add(found);
                }
                RefreshProductsView();

                // 4. Участие в акциях по этому товару + пересчёт акций
                await LoadParticipationAsync(found.Code.ToString());
                await RecalcActionsAsync();
            }
            catch (Exception ex)
            {
                await DisplayAlert("Ошибка", ex.Message, "OK");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  КЛИЕНТ (паритет FindClientByCode; правило 5% — как в CheckPage)
        // ══════════════════════════════════════════════════════════════
        private async void OnClientCodeCompleted(object sender, EventArgs e)
        {
            string input = (ClientCodeEntry.Text ?? "").Trim();
            ClientCodeEntry.Text = "";

            _clientCode = "";
            _clientHasDiscount = false;
            ClientLabel.Text = "";
            ClientLabel.TextColor = Colors.DarkGreen;
            ClientCodeEntry.IsEnabled = true;

            if (input.Length == 0)
            {
                RefreshProductsView();
                await RecalcActionsAsync();
                return;
            }

            if (input.Length != 10 && input.Length != 13)
            {
                await DisplayAlert("Карта клиента", "Нужно 10 или 13 цифр (телефон или код карты)", "OK");
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
                                ClientLabel.Text = "Клиент: " + reader.GetValue(1);
                                ClientLabel.TextColor = Colors.DarkGreen;
                                ClientCodeEntry.IsEnabled = false;   // паритет десктопа: блокируем до «Очистить»
                            }
                        }
                    }
                }

                if (!_clientHasDiscount)
                {
                    ClientLabel.Text = "Клиент не найден";
                    ClientLabel.TextColor = Colors.Gray;
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Ошибка", ex.Message, "OK");
            }

            RefreshProductsView();          // цены в наборе: с −5% или полные
            await RecalcActionsAsync();     // пересчёт с учётом клиента
        }

        // ══════════════════════════════════════════════════════════════
        //  УДАЛЕНИЕ СТРОКИ НАБОРА (паритет DeleteSelectedTovarRow:
        //  удалили → перерисовали → пересчитали)
        // ══════════════════════════════════════════════════════════════
        private async void OnDeleteProductClicked(object sender, EventArgs e)
        {
            if (sender is not Button btn || btn.BindingContext is not VerifyProductItem item) return;

            var found = _products.FirstOrDefault(p => p.Code == item.Code);
            if (found != null) _products.Remove(found);

            RefreshProductsView();
            await RecalcActionsAsync();
        }

        // ══════════════════════════════════════════════════════════════
        //  СБРОСИТЬ ВСЁ (кнопка «Сбросить всё» в строке клиента; паритет ClearAll):
        //  очищает ВЕСЬ черновик — товары, результат, участие, акционные ШК,
        //  клиента и оба поля ввода. Это НЕ очистка поля ШК (для неё — бекспейс).
        // ══════════════════════════════════════════════════════════════
        private async void OnClearClicked(object sender, EventArgs e)
        {
            _products.Clear();
            _executeItems.Clear();
            _participationItems.Clear();
            _actionBarcodeList.Clear();

            _clientCode = "";
            _clientHasDiscount = false;
            ClientLabel.Text = "";
            ClientLabel.TextColor = Colors.DarkGreen;
            ClientCodeEntry.Text = "";
            ClientCodeEntry.IsEnabled = true;
            SumLabel.Text = "0.00 ₽";
            BarcodeEntry.Text = "";

            Dispatcher.Dispatch(() => { try { BarcodeEntry.Focus(); } catch { /* не критично */ } });
            await Task.CompletedTask;
        }

        // ══════════════════════════════════════════════════════════════
        //  ПОИСК ТОВАРА В ЛОКАЛЬНОЙ БД (паритет FindBarcodeOrCodeInTovar;
        //  схема запросов — как в CheckPage)
        // ══════════════════════════════════════════════════════════════
        private static async Task<VerifyProductItem> FindProductAsync(string barcode)
        {
            using (var conn = MainStaticClass.GetLocalSQLiteConnection())
            {
                await conn.OpenAsync();

                string query;
                using var cmd = new SqliteCommand { Connection = conn };

                if (barcode.Length > 6)
                {
                    query = @"
                        SELECT t.code, t.name, t.retail_price
                        FROM barcode b
                        LEFT JOIN tovar t ON b.tovar_code = t.code
                        WHERE b.barcode = @v
                          AND t.its_deleted = 0
                          AND t.retail_price <> 0
                        LIMIT 1";
                    cmd.Parameters.AddWithValue("@v", barcode);
                }
                else
                {
                    query = @"
                        SELECT t.code, t.name, t.retail_price
                        FROM tovar t
                        WHERE t.its_deleted = 0
                          AND t.its_certificate = 0
                          AND t.retail_price <> 0
                          AND t.code = @v
                        LIMIT 1";
                    cmd.Parameters.AddWithValue("@v", long.TryParse(barcode, out long c) ? c : 0L);
                }

                cmd.CommandText = query;
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        return new VerifyProductItem
                        {
                            Code = Convert.ToInt64(reader.GetValue(0)),
                            Name = reader.GetValue(1).ToString(),
                            Quantity = 1,
                            Price = Convert.ToDecimal(reader.GetValue(2))
                        };
                    }
                }
            }
            return null;
        }

        // ══════════════════════════════════════════════════════════════
        //  АКЦИОННЫЙ ЛИ ШК (паритет CheckAction: длина > 4 — barcode,
        //  ≤ 4 — promo_code; дата акции проверяется в C#, т.к. в локальной
        //  SQLite даты текстовые — тот же приём, что в CheckPage)
        // ══════════════════════════════════════════════════════════════
        private async Task<bool> IsActionBarcodeAsync(string input)
        {
            try
            {
                using (var conn = MainStaticClass.GetLocalSQLiteConnection())
                {
                    await conn.OpenAsync();
                    using (var cmd = new SqliteCommand { Connection = conn })
                    {
                        if (input.Length > 4)
                            cmd.CommandText = "SELECT date_started, date_end FROM action_header WHERE TRIM(barcode) = @v";
                        else
                        {
                            if (!await PromoCodeColumnExistsAsync(conn)) return false;
                            cmd.CommandText = "SELECT date_started, date_end FROM action_header WHERE TRIM(promo_code) = @v";
                        }
                        cmd.Parameters.AddWithValue("@v", input);

                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            DateTime today = DateTime.Today;
                            while (await r.ReadAsync())
                            {
                                DateTime? started = ParseActionDate(r.IsDBNull(0) ? "" : r.GetValue(0).ToString());
                                DateTime? end = ParseActionDate(r.IsDBNull(1) ? "" : r.GetValue(1).ToString());
                                if (started.HasValue && end.HasValue &&
                                    started.Value.Date <= today && today <= end.Value.Date)
                                    return true;
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

        // колонка promo_code появилась в схеме позже — проверяем один раз за сеанс страницы
        private async Task<bool> PromoCodeColumnExistsAsync(SqliteConnection conn)
        {
            if (_promoCodeColumnChecked) return _promoCodeColumnExists;
            _promoCodeColumnChecked = true;
            try
            {
                using (var cmd = new SqliteCommand(
                    "SELECT COUNT(*) FROM pragma_table_info('action_header') WHERE name = 'promo_code'", conn))
                {
                    _promoCodeColumnExists = Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
                }
            }
            catch
            {
                _promoCodeColumnExists = false;
            }
            return _promoCodeColumnExists;
        }

        // те же форматы дат, что в движке акций и CheckPage
        private static DateTime? ParseActionDate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();
            if (s.Length > 10) s = s.Substring(0, 10);
            if (DateTime.TryParseExact(s,
                    new[] { "yyyy-MM-dd", "dd.MM.yyyy", "dd-MM-yyyy", "yyyy.MM.dd" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d))
                return d;
            return null;
        }

        // ══════════════════════════════════════════════════════════════
        //  УЧАСТИЕ В МЕХАНИЧЕСКИХ АКЦИЯХ (паритет GetParticipationMechanicalAction:
        //  action_table по коду товара ИЛИ по ШК; дата — в C#)
        // ══════════════════════════════════════════════════════════════
        private async Task LoadParticipationAsync(string codeOrBarcode)
        {
            _participationItems.Clear();
            try
            {
                using (var conn = MainStaticClass.GetLocalSQLiteConnection())
                {
                    await conn.OpenAsync();
                    using (var cmd = new SqliteCommand { Connection = conn })
                    {
                        cmd.CommandText = @"
                            SELECT DISTINCT ah.num_doc, ah.comment, ah.tip, ah.execution_order,
                                            ah.date_started, ah.date_end
                            FROM action_table at
                            LEFT JOIN action_header ah ON at.num_doc = ah.num_doc
                            WHERE (CAST(at.code_tovar AS TEXT) = @code
                                   OR at.code_tovar IN (SELECT tovar_code FROM barcode WHERE barcode = @code))
                              AND ah.num_doc IS NOT NULL
                            ORDER BY ah.execution_order";
                        cmd.Parameters.AddWithValue("@code", codeOrBarcode);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            DateTime today = DateTime.Today;
                            var rows = new List<VerifyParticipationItem>();
                            while (await reader.ReadAsync())
                            {
                                DateTime? started = ParseActionDate(reader.IsDBNull(4) ? "" : reader.GetValue(4).ToString());
                                DateTime? end = ParseActionDate(reader.IsDBNull(5) ? "" : reader.GetValue(5).ToString());
                                if (!started.HasValue || !end.HasValue ||
                                    !(started.Value.Date <= today && today <= end.Value.Date))
                                    continue;   // акция не в периоде — не показываем (паритет десктопа)

                                rows.Add(new VerifyParticipationItem
                                {
                                    NumDoc = Convert.ToInt32(reader.GetValue(0)),
                                    Comment = reader.IsDBNull(1) ? "" : reader.GetValue(1).ToString().Trim(),
                                    Tip = Convert.ToInt32(reader.GetValue(2)),
                                    ExecutionOrder = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3))
                                });
                            }
                            foreach (var row in rows) _participationItems.Add(row);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Участие в акциях: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  ПЕРЕСЧЁТ АКЦИЙ (паритет ToDefineTheActionDt):
        //  1) копия набора → движок ProcessingOfActions (акц. ШК → клиент → обычные);
        //  2) 5% клиента — только на строки БЕЗ акций (правило CheckPage);
        //  3) «Выполненные товары» + ИТОГО по результату;
        //  4) сообщения движка — как на десктопе (show_messages=true).
        //  В БД ничего не пишется.
        // ══════════════════════════════════════════════════════════════
        private async Task RecalcActionsAsync()
        {
            // скан во время пересчёта не теряем: помечаем и считаем ещё раз
            if (_recalcBusy) { _recalcPending = true; return; }
            _recalcBusy = true;

            try
            {
                bool runAgain;
                do
                {
                    runAgain = false;
                    int gen = ++_recalcGeneration;

                    // набранные строки → рабочие строки движка (всегда от ПОЛНОЙ цены)
                    var lines = _products.Select(p => new CheckLine
                    {
                        TovarCode = p.Code,
                        TovarName = p.Name,
                        Quantity = p.Quantity,
                        Price = p.Price,
                        PriceAtDiscount = p.Price,
                        SumFull = Math.Round(p.Quantity * p.Price, 2, MidpointRounding.AwayFromZero),
                        SumAtDiscount = Math.Round(p.Quantity * p.Price, 2, MidpointRounding.AwayFromZero),
                        Marking = "0"
                    }).ToList();

                    ActionCalcResult result;
                    try
                    {
                        result = await Task.Run(() =>
                        {
                            using (var conn = MainStaticClass.GetLocalSQLiteConnection())
                            {
                                conn.Open();
                                var engine = new ProcessingOfActions(conn);
                                return engine.Process(lines, _clientCode, new List<string>(_actionBarcodeList));
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        await DisplayAlert("Ошибка при обработке акций", ex.Message, "OK");
                        return;
                    }

                    if (gen != _recalcGeneration) return;   // пока считали, набор изменился

                    // 5% клиента — только на строки без акций (правило десктопа/CheckPage)
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

                    // «Выполненные товары»
                    _executeItems.Clear();
                    foreach (var l in result.Lines)
                    {
                        _executeItems.Add(new VerifyExecuteItem
                        {
                            Code = l.TovarCode,
                            Name = l.TovarName,
                            Quantity = l.Quantity,
                            PriceAtDiscount = l.PriceAtDiscount,
                            SumAtDiscount = l.SumAtDiscount,
                            Action2 = l.Action2,
                            PromoDescription = l.PromoDescription ?? ""
                        });
                    }

                    // ИТОГО — по результату движка (паритет Calculate по dt2)
                    decimal total = Math.Round(result.Lines.Sum(l => l.SumAtDiscount), 2, MidpointRounding.AwayFromZero);
                    SumLabel.Text = total.ToString("F2", CultureInfo.InvariantCulture) + " ₽";

                    // сообщения движка — паритет десктопного окна (show_messages=true);
                    // если уже ждёт новый пересчёт — не мучаем кассира повторным окном
                    if (result.Messages.Count > 0 && !_recalcPending)
                        await DisplayAlert("Акции", string.Join("\n", result.Messages), "OK");

                    if (_recalcPending)
                    {
                        _recalcPending = false;
                        runAgain = true;
                    }
                } while (runAgain);
            }
            finally
            {
                _recalcBusy = false;
            }
        }

        // полная перерисовка набора (как RefreshTovarTable десктопа —
        // строка с изменённым количеством/ценой обновляется целиком)
        private void RefreshProductsView()
        {
            TovarCollectionView.ItemsSource = null;
            TovarCollectionView.ItemsSource = _products;
        }
    }
}
