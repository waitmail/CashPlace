// ============================================================================
// PrinterPage.xaml.cs — служебная страница ФР АТОЛ для планшета CashPlace.
//
// Что здесь есть (для начала):
//   - ОБНОВИТЬ / ПРОВЕРИТЬ СВЯЗЬ С ФР  -> AtolFiscalPrinter.TestConnectionAsync()
//   - X-ОТЧЁТ                          -> AtolFiscalPrinter.PrintXReportAsync()
//   - Z-ОТЧЁТ (закрытие смены)         -> AtolFiscalPrinter.PrintZReportAsync()
//   - ВНЕСЕНИЕ наличных                -> AtolFiscalPrinter.CashIncomeAsync(sum)
//   - ВЫНЕСЕНИЕ наличных               -> AtolFiscalPrinter.CashOutcomeAsync(sum)
//   - НАСТРОЙКИ АТОЛ (родное окно)     -> AtolFiscalPrinter.RunNativeAtolSettingsFlowAsync()
//
// Файл кладётся в корень проекта (рядом с MainPage.xaml).
// AtolFiscalPrinter живёт в Platforms/Android/ (namespace CashPlace), поэтому
// все обращения к нему — только под #if ANDROID. На Windows-цели страница
// открывается, но операции показывают «доступно только на планшете».
//
// Ошибки ДТО-10 (X/Z/внесение/вынесение) сами показывают DisplayAlert внутри
// AtolFiscalPrinter; здесь дополнительно пишем итог в LblStatus.
// ============================================================================

using Microsoft.Maui.Controls;

namespace CashPlace
{
    public partial class PrinterPage : ContentPage
    {
        public PrinterPage()
        {
            InitializeComponent();
        }

        // ------------------------------------------------------------------
        // Помощники
        // ------------------------------------------------------------------

        private void SetBusy(bool busy)
        {
            BtnRefresh.IsEnabled = !busy;
            BtnX.IsEnabled = !busy;
            BtnZ.IsEnabled = !busy;
            BtnCashIn.IsEnabled = !busy;
            BtnCashOut.IsEnabled = !busy;
            BtnAtolSettings.IsEnabled = !busy;
        }

        /// <summary>Строка "1234,56 руб" / "1234.56" -> положительное число.</summary>
        private static bool TryParseSum(string text, out double sum)
        {
            sum = 0.0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            string norm = text.Trim()
                              .Replace(" ", "")
                              .Replace("\u00A0", "")
                              .Replace(',', '.');
            bool ok = double.TryParse(norm,
                                      System.Globalization.NumberStyles.Float,
                                      System.Globalization.CultureInfo.InvariantCulture,
                                      out sum);
            return ok && sum > 0.0;
        }

        // ------------------------------------------------------------------
        // Обновить / проверить связь с ФР
        // ------------------------------------------------------------------

        private async void BtnRefresh_Clicked(object sender, System.EventArgs e)
        {
#if ANDROID
            SetBusy(true);
            LblStatus.Text = "Подключение к ФР…";
            try
            {
                string status = await AtolFiscalPrinter.TestConnectionAsync();
                LblStatus.Text = status;
            }
            catch (System.Exception ex)
            {
                LblStatus.Text = "НЕТ СВЯЗИ С ФР (исключение)\n" + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
#else
            await DisplayAlert("ФР АТОЛ", "Операции с ФР доступны только на планшете (Android).", "OK");
#endif
        }

        // ------------------------------------------------------------------
        // СБОРКА 8: родное окно настроек драйвера АТОЛ (SettingsActivity)
        // ------------------------------------------------------------------

        private async void BtnAtolSettings_Clicked(object sender, System.EventArgs e)
        {
#if ANDROID
            SetBusy(true);
            LblStatus.Text = "Открываю родные настройки АТОЛ…";
            try
            {
                string report = await AtolFiscalPrinter.RunNativeAtolSettingsFlowAsync();
                LblStatus.Text = report;
            }
            catch (System.Exception ex)
            {
                LblStatus.Text = "Настройки АТОЛ: исключение\n" + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
#else
            await DisplayAlert("ФР АТОЛ", "Настройки АТОЛ доступны только на планшете (Android).", "OK");
#endif
        }

        // ------------------------------------------------------------------
        // X-отчёт (смена НЕ закрывается)
        // ------------------------------------------------------------------

        private async void BtnX_Clicked(object sender, System.EventArgs e)
        {
#if ANDROID
            if (!await DisplayAlert("X-ОТЧЁТ",
                    "Распечатать X-отчёт?\nСмена остаётся открытой.",
                    "ПЕЧАТЬ", "ОТМЕНА"))
            {
                return;
            }

            SetBusy(true);
            LblStatus.Text = "Печать X-отчёта…";
            try
            {
                FiscalPrintResult res = await AtolFiscalPrinter.PrintXReportAsync();
                if (res.Success)
                {
                    LblStatus.Text = "X-ОТЧЁТ НАПЕЧАТАН\n" +
                                     System.DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");
                }
                else
                {
                    LblStatus.Text = "X-ОТЧЁТ НЕ НАПЕЧАТАН\nОшибка " + res.ErrorCode +
                                     ": " + res.ErrorDescription;
                }
            }
            catch (System.Exception ex)
            {
                LblStatus.Text = "Исключение при X-отчёте:\n" + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
#else
            await DisplayAlert("ФР АТОЛ", "Операции с ФР доступны только на планшете (Android).", "OK");
#endif
        }

        // ------------------------------------------------------------------
        // Z-отчёт (ЗАКРЫТИЕ смены!) — с обязательным подтверждением
        // ------------------------------------------------------------------

        private async void BtnZ_Clicked(object sender, System.EventArgs e)
        {
#if ANDROID
            if (!await DisplayAlert("Z-ОТЧЁТ — ВНИМАНИЕ!",
                    "Будет выполнено ЗАКРЫТИЕ СМЕНЫ.\n" +
                    "Печатается Z-отчёт, смена закрывается.\n\nПродолжить?",
                    "ЗАКРЫТЬ СМЕНУ", "ОТМЕНА"))
            {
                return;
            }

            SetBusy(true);
            LblStatus.Text = "Закрытие смены (Z-отчёт)…";
            try
            {
                FiscalPrintResult res = await AtolFiscalPrinter.PrintZReportAsync();
                if (res.Success)
                {
                    LblStatus.Text = "СМЕНА ЗАКРЫТА, Z-ОТЧЁТ НАПЕЧАТАН\n" +
                                     System.DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");
                }
                else
                {
                    LblStatus.Text = "СМЕНА НЕ ЗАКРЫТА\nОшибка " + res.ErrorCode +
                                     ": " + res.ErrorDescription;
                }
            }
            catch (System.Exception ex)
            {
                LblStatus.Text = "Исключение при закрытии смены:\n" + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
#else
            await DisplayAlert("ФР АТОЛ", "Операции с ФР доступны только на планшете (Android).", "OK");
#endif
        }

        // ------------------------------------------------------------------
        // Внесение наличных
        // ------------------------------------------------------------------

        private async void BtnCashIn_Clicked(object sender, System.EventArgs e)
        {
#if ANDROID
            if (!TryParseSum(EntSum.Text, out double sum))
            {
                await DisplayAlert("Внесение", "Введите сумму больше нуля.", "OK");
                return;
            }

            if (!await DisplayAlert("ВНЕСЕНИЕ НАЛИЧНЫХ",
                    string.Format("Внести в денежный ящик {0:N2} руб.?", sum),
                    "ВНЕСТИ", "ОТМЕНА"))
            {
                return;
            }

            SetBusy(true);
            LblStatus.Text = "Внесение наличных…";
            try
            {
                FiscalPrintResult res = await AtolFiscalPrinter.CashIncomeAsync(sum);
                if (res.Success)
                {
                    LblStatus.Text = "ВНЕСЕНИЕ " + sum.ToString("N2") + " руб. ВЫПОЛНЕНО\n" +
                                     System.DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");
                    EntSum.Text = "";
                }
                else
                {
                    LblStatus.Text = "ВНЕСЕНИЕ НЕ ВЫПОЛНЕНО\nОшибка " + res.ErrorCode +
                                     ": " + res.ErrorDescription;
                }
            }
            catch (System.Exception ex)
            {
                LblStatus.Text = "Исключение при внесении:\n" + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
#else
            await DisplayAlert("ФР АТОЛ", "Операции с ФР доступны только на планшете (Android).", "OK");
#endif
        }

        // ------------------------------------------------------------------
        // Вынесение наличных (инкассация)
        // ------------------------------------------------------------------

        private async void BtnCashOut_Clicked(object sender, System.EventArgs e)
        {
#if ANDROID
            if (!TryParseSum(EntSum.Text, out double sum))
            {
                await DisplayAlert("Вынесение", "Введите сумму больше нуля.", "OK");
                return;
            }

            if (!await DisplayAlert("ВЫНЕСЕНИЕ НАЛИЧНЫХ",
                    string.Format("Вынести из денежного ящика {0:N2} руб.?", sum),
                    "ВЫНЕСТИ", "ОТМЕНА"))
            {
                return;
            }

            SetBusy(true);
            LblStatus.Text = "Вынесение наличных…";
            try
            {
                FiscalPrintResult res = await AtolFiscalPrinter.CashOutcomeAsync(sum);
                if (res.Success)
                {
                    LblStatus.Text = "ВЫНЕСЕНИЕ " + sum.ToString("N2") + " руб. ВЫПОЛНЕНО\n" +
                                     System.DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");
                    EntSum.Text = "";
                }
                else
                {
                    LblStatus.Text = "ВЫНЕСЕНИЕ НЕ ВЫПОЛНЕНО\nОшибка " + res.ErrorCode +
                                     ": " + res.ErrorDescription;
                }
            }
            catch (System.Exception ex)
            {
                LblStatus.Text = "Исключение при вынесении:\n" + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
#else
            await DisplayAlert("ФР АТОЛ", "Операции с ФР доступны только на планшете (Android).", "OK");
#endif
        }
    }
}