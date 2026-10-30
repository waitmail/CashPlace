using Microsoft.Data.Sqlite;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace CashPlace;

public partial class ChecksPage : ContentPage
{
    public ObservableCollection<CheckItem> CheckItems { get; set; } = new();
    private DateTime _currentDate = DateTime.Today;
    

    public ChecksPage()
    {
        InitializeComponent();
        BindingContext = this;
        datePicker.Date = _currentDate;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadDocumentsAsync(); // Обновляем список при каждом возврате на эту страницу
    }

    private async void OnRefreshClicked(object sender, EventArgs e)
    {
        _currentDate = datePicker.Date;
        await LoadDocumentsAsync();
    }
    
    // ✅ НОВЫЙ ОБРАБОТЧИК НАЖАТИЯ НА КАРТОЧКУ ЧЕКА
    private async void OnCheckItemTapped(object sender, TappedEventArgs e)
    {
        try
        {
            // Получаем элемент, на который нажали
            if (sender is Frame frame && frame.BindingContext is CheckItem selectedCheck)
            {
                // Открываем страницу чека с параметрами
                await Navigation.PushAsync(new CheckPage(selectedCheck.DocumentNumber, selectedCheck.DateTimeWrite));
            }
        }
        catch (Exception ex)
        {
            // Если при открытии чека падает ошибка, показываем её, чтобы не падать молча
            await DisplayAlert("Ошибка открытия", ex.Message, "OK");
        }
    }

    private async void OnNewCheckClicked(object sender, EventArgs e)
    {
        // Открываем страницу чека без параметров (создаем новый)
        await Navigation.PushAsync(new CheckPage());
    }
    
        // ★ ИТОГИ ПО ФОРМАМ ОПЛАТЫ за выбранный день — отдельным запросом.
    //   НЕ по видимому списку: список может быть обрезан LIMIT 3.
    //   ★ Фильтр ТОТ ЖЕ, что у списка — date_time_write (колонка date давала пустой набор).
    private async Task LoadDayTotalsAsync(string startDate, string endDate)
    {
        try
        {
            using var conn = MainStaticClass.GetLocalSQLiteConnection();
            await conn.OpenAsync();

            const string sql = @"
            SELECT
                COALESCE(SUM(CASE WHEN its_deleted = 0 AND pay_type = 0 THEN cash ELSE 0 END), 0) AS cash_sum,
                COALESCE(SUM(CASE WHEN its_deleted = 0 AND pay_type = 1 THEN cash ELSE 0 END), 0) AS qr_sum,
                SUM(CASE WHEN its_deleted = 0 THEN 1 ELSE 0 END)                                  AS cnt_ok,
                COALESCE(SUM(CASE WHEN its_deleted = 0 THEN cash ELSE 0 END), 0)                  AS total_ok,
                SUM(CASE WHEN its_deleted = 1 THEN 1 ELSE 0 END)                                  AS cnt_del,
                COALESCE(SUM(CASE WHEN its_deleted = 1 THEN cash ELSE 0 END), 0)                  AS total_del
            FROM checks_header
            WHERE date_time_write >= @startDate AND date_time_write < @endDate";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@startDate", startDate);
            cmd.Parameters.AddWithValue("@endDate", endDate);

            using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                decimal cashSum = Convert.ToDecimal(r.GetValue(0));
                decimal qrSum = Convert.ToDecimal(r.GetValue(1));
                int cntOk = r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2));
                decimal totalOk = Convert.ToDecimal(r.GetValue(3));
                int cntDel = r.IsDBNull(4) ? 0 : Convert.ToInt32(r.GetValue(4));
                decimal totalDel = Convert.ToDecimal(r.GetValue(5));

                TotalsCashLabel.Text = $"Наличные: {cashSum:F2} ₽";
                TotalsQrLabel.Text = $"QR-код: {qrSum:F2} ₽";
                TotalsTotalLabel.Text = $"ИТОГО: {totalOk:F2} ₽";
                int cntAll = cntOk + cntDel;
                TotalsCountLabel.Text = cntDel > 0
                    ? $"Чеков: {cntOk} из {cntAll} (удал. {cntDel})"
                    : $"Чеков: {cntOk}";

                if (cntDel > 0)
                {
                    TotalsDeletedLabel.Text = $"Удалено: {cntDel} чек. на {totalDel:F2} ₽";
                    TotalsDeletedLabel.IsVisible = true;
                }
                else
                    TotalsDeletedLabel.IsVisible = false;
            }
        }
        catch (Exception ex)
        {
            // ★ ВРЕМЕННО для отладки: ошибка показывается на экране, а не прячется в лог.
            //   Когда итоги заработают — вернуть:
            //   Debug.WriteLine($"Ошибка итогов: {ex.Message}");
            await DisplayAlert("Ошибка итогов", ex.Message, "OK");
        }
    }

    private async Task LoadDocumentsAsync()
    {
        try
        {
            bool showLast3 = checkBox_show_3_last_checks.IsChecked;
            DateTime selectedDate = datePicker.Date;

            // ★ даты считаем ДО Task.Run — нужны и списку, и итогам
            string startDateStr = selectedDate.ToString("yyyy-MM-dd");
            string endDateStr = selectedDate.AddDays(1).ToString("yyyy-MM-dd");

            NoDataLabel.IsVisible = false;

            var checkItems = await Task.Run(async () =>
            {
                var items = new List<CheckItem>();
                try
                {
                    using (var conn = MainStaticClass.GetLocalSQLiteConnection())
                    {
                        await conn.OpenAsync();

                        string myQuery = @"
                        SELECT its_deleted, date_time_write, client, cash, remainder, comment, 
                               its_print, check_type, document_number, its_print_p, extra, is_sent
                        FROM checks_header 
                        WHERE date_time_write >= @startDate AND date_time_write < @endDate 
                          AND its_deleted < 2 
                        ORDER BY date_time_write";

                        if (showLast3)
                            myQuery += " DESC LIMIT 3";

                        using (var command = new SqliteCommand(myQuery, conn))
                        {
                            command.Parameters.AddWithValue("@startDate", startDateStr);
                            command.Parameters.AddWithValue("@endDate", endDateStr);

                            using (var reader = await command.ExecuteReaderAsync())
                            {
                                while (await reader.ReadAsync())
                                {
                                    var checkItem = new CheckItem();
                                    checkItem.ItsDeleted =
                                        reader.IsDBNull(0) ? 0 : Convert.ToDecimal(reader.GetValue(0));

                                    string dateStr = reader.IsDBNull(1) ? "" : reader.GetString(1);
                                    if (DateTime.TryParse(dateStr, out DateTime parsedDate))
                                        checkItem.DateTimeWrite = parsedDate;

                                    checkItem.ClientName = reader.IsDBNull(2) ? "" : reader.GetString(2).Trim();
                                    checkItem.Cash = reader.IsDBNull(3) ? 0 : Convert.ToDecimal(reader.GetValue(3));
                                    checkItem.Comment = reader.IsDBNull(5) ? "" : reader.GetString(5).Trim();

                                    if (!reader.IsDBNull(7))
                                    {
                                        long checkTypeVal = Convert.ToInt64(reader.GetValue(7));
                                        checkItem.CheckType = checkTypeVal switch
                                        {
                                            0 => "Продажа", 1 => "Возврат", 2 => "Коррекция", _ => "Неизвестно"
                                        };
                                    }

                                    checkItem.DocumentNumber = reader.IsDBNull(8) ? "" : reader.GetValue(8).ToString();
                                    checkItem.IsSent =
                                        !reader.IsDBNull(11) && Convert.ToInt32(reader.GetValue(11)) == 1;
                                    items.Add(checkItem);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Ошибка SQLite: {ex.Message}");
                }

                return items;
            });

            CheckItems.Clear();

            if (checkItems.Count == 0)
            {
                NoDataLabel.Text = "Нет данных за выбранную дату";
                NoDataLabel.IsVisible = true;
            }
            else
            {
                NoDataLabel.IsVisible = false;
                foreach (var item in checkItems)
                    CheckItems.Add(item);

                ChecksCollectionView.ScrollTo(0);
            }

            // ★ ИТОГИ — строго ПОСЛЕ заполнения списка, на UI-потоке.
            //   Метод открывает СВОЁ соединение, поэтому вызываем СНАРУЖИusing
            await LoadDayTotalsAsync(startDateStr, endDateStr);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }
    }
}