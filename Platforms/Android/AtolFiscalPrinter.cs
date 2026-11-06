// ============================================================================
// AtolFiscalPrinter.cs — порт десктопного PrintingUsingLibraries.cs (Cash8Avalon)
// на планшет CashPlace (.NET MAUI, Android, ФР АТОЛ, ДТО-10).
//
// ПЕЧАТЬ ЧЕКОВ, ОПЛАЧЕННЫХ ПО QR (СБП): безналичная часть чека передаётся ФР
// как электронная оплата (LIBFPTR_PT_ELECTRONICALLY) — полный паритет с десктопом.
//
// Требования (уже сделаны):
//  - Platforms/Android/Jars/libfptr10.aar + csproj:
//      <AndroidLibrary Remove="Platforms\Android\Jars\libfptr10.aar" />
//      <AndroidLibrary Include="Platforms\Android\Jars\libfptr10.aar" Bind="true" />
//      <TransformFile Include="Transforms\Metadata.xml" />
//  - Transforms/Metadata.xml: класс Fptr переименован в FptrImpl (устранение
//    коллизии с peer-классом констант интерфейса IFptr).
//  - AndroidManifest.xml (Platforms/Android/AndroidManifest.xml), внутрь <manifest ...>:
//      <uses-feature android:name="android.hardware.usb.host" android:required="false" />
//
// ВАЖНЫЕ ОТЛИЧИЯ ОТ ДЕСКТОПА:
//  1. Неймспейс биндинга — RU.Atol.Drivers10.Fptr (не Atol.Drivers10.Fptr).
//  2. Констант нет в отдельном классе Constants: AtolConstants.X  ->  IFptr.X.
//  3. Класс драйвера — FptrImpl (переименован Metadata.xml), константы и методы — IFptr.
//  4. Все вызовы ДТО-10 блокирующие => фискальный сценарий выполняется в Task.Run.
//  5. MessageBoxHelper -> системные DisplayAlert на главном потоке.
//  6. Реклама из Postgres и проверка Честного знака на планшете пока НЕ портированы
//     (помечены TODO). Код маркировки передаётся в ФР как есть.
//  7. Флаг its_print в БД планшета пока не портирован: защита от повторной печати —
//     HashSet в памяти (от двойного клика). TODO: добавить колонку its_print в checks_header.
// ============================================================================

using Android.App;
using Android.Content;
using Android.Hardware;
using RU.Atol.Drivers10.Fptr;

namespace CashPlace
{
    // ==========================================================================
    // Настройки, которые на десктопе жили в MainStaticClass.
    // Заполнить один раз при старте приложения (MainActivity/App).
    // ==========================================================================
    public static class AtolPrinterConfig
    {
        public static string OperatorName = "";      // MainStaticClass.Cash_Operator (тег 1021)
        public static string OperatorInn = "";       // MainStaticClass.CashOperatorInn (тег 1203)
        public static int SystemTaxation = 4;        // 1=ОСН, 2=УСН Доход-Расход, 4=УСН Доход (семантика десктопа)
        public static int NdsIp = 0;                 // для УСН: 0=без НДС, 5 или 7 (MainStaticClass.GetNdsIp)
        public static string NickShop = "";          // для строки "Магазин-Касса-НомерЧека"
        public static int CashDeskNumber = 1;

        /// <summary>Подключите ваш логгер: (тег, сообщение). Например аналог write_event_in_log.</summary>
        public static event System.Action<string, string> LogEvent;

        internal static void Log(string tag, string message)
        {
            try
            {
                LogEvent?.Invoke(tag, message);
                System.Diagnostics.Debug.WriteLine("[ATOL:" + tag + "] " + message);
            }
            catch { }
        }
    }

    // ==========================================================================
    // Позиция чека для фискализации (заполняется из ProductItem/CheckItem).
    // ==========================================================================
    public sealed class FiscalPosition
    {
        public string Code;                 // код товара
        public string Tovar;                // наименование
        public double PriceAtDiscount;      // цена с учётом скидки
        public double Quantity;             // количество
        public bool IsSertificate;          // сертификат -> предмет расчёта 1214=3 (и НДС 122/120 при ОСН)
        public bool IsExcise;               // акциз -> 1212=2 (аналог its_excise)
        public bool IsWeighted;             // false = штуки (piece), true = килограммы
        public int StavkaNds;               // ставка НДС из товара: 0/5/7/10/18/20/22 (аналог get_tovar_nds)
        public string Mark;                 // код маркировки ("" если нет); спец-замена vasya2021 -> ' сохранена
    }

    // ==========================================================================
    // Запрос на печать чека.
    // ==========================================================================
    public sealed class FiscalReceiptRequest
    {
        public string DocKey;               // номер чека (гвард от повторной печати + строка "М-К-N")
        public int ReceiptType;             // 0 = продажа (QR-чек), 1 = возврат продажи
        public System.Collections.Generic.List<FiscalPosition> Positions;
        public double SumCash;              // наличные (checks_header.cash_money)
        public double SumElectron;          // безналичные, включая СБП/QR (checks_header.non_cash_money)
        public double SumPrepaid;           // сертификаты (checks_header.sertificate_money)
        public string ClientContact;        // телефон +7XXXXXXXXXX или email (тег 1008); можно пусто
        public string DiscountNote;         // строка о скидке (печатается в конце чека); можно пусто
    }

    // ==========================================================================
    // Результат печати.
    // ==========================================================================
    public sealed class FiscalPrintResult
    {
        public bool Success;
        public int ErrorCode;
        public string ErrorDescription;

        public static FiscalPrintResult Ok()
        {
            return new FiscalPrintResult { Success = true, ErrorCode = 0, ErrorDescription = "" };
        }

        public static FiscalPrintResult Fail(int code, string description)
        {
            return new FiscalPrintResult { Success = false, ErrorCode = code, ErrorDescription = description ?? "" };
        }
    }

    // ==========================================================================
    // Основной класс печати.
    // ==========================================================================
    public static class AtolFiscalPrinter
    {
        private static readonly object _fptrLock = new object();
        private static IFptr _fptr;

        // ЗАЩИТА 1 (паритет с десктопом): от двойного клика — документы "в печати"
        private static readonly System.Collections.Generic.HashSet<string> _currentlyPrintingDocs =
            new System.Collections.Generic.HashSet<string>();

        private const string ActionUsbPermission = "com.yourname.cash8mobile.USB_PERMISSION";
        private const int AtolUsbVendorId = 0x2E83;  // VID устройств АТОЛ
        private const int FtdiUsbVendorId = 0x0403;  // ФР на мостах FTDI (часть АТОЛ 30Ф/11Ф)

        // ------------------------------------------------------------------------
        // Подключение: единственный экземпляр IFptr на всё приложение (паритет
        // с MainStaticClass.FPTR). Настройки — USB.
        // ------------------------------------------------------------------------
        private static IFptr GetFptr()
        {
            lock (_fptrLock)
            {
                if (_fptr == null)
                {
                    Context context = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
                                      ?? Android.App.Application.Context;

                    // Если в вашем биндинге у FptrImpl конструктор без параметров — уберите аргумент.
                    _fptr = new FptrImpl(context);

                    // ДТО-10 на Android: настройки передаются JSON-объектом.
                    // USB-подключение к ФР:
                    _fptr.setSettings(new Org.Json.JSONObject("{\"type\":\"usb\"}"));
                    _fptr.applySettings();

                    AtolPrinterConfig.Log("Connect", "Драйвер АТОЛ создан, настройки USB применены");
                }
                return _fptr;
            }
        }

        private static bool EnsureOpen(IFptr fptr, out int errorCode, out string errorDescription)
        {
            errorCode = 0;
            errorDescription = "";
            if (fptr.isOpened())
            {
                return true;
            }
            EnsureUsbPermissions();
            if (!fptr.open())
            {
                errorCode = fptr.errorCode();
                errorDescription = fptr.errorDescription();
                AtolPrinterConfig.Log("Connect", "Не удалось открыть связь с ФР: " + errorCode + " " + errorDescription);
                return false;
            }
            return true;
        }

        // Android требует разрешения на работу с USB-устройством. Драйвер обычно
        // запрашивает его сам, но на всякий случай просим заранее (идемпотентно).
        private static void EnsureUsbPermissions()
        {
            try
            {
                Context context = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
                                  ?? Android.App.Application.Context;
                UsbManager usbManager = (UsbManager)context.GetSystemService(Context.UsbService);
                if (usbManager == null || usbManager.DeviceList == null)
                {
                    return;
                }

                foreach (UsbDevice device in usbManager.DeviceList.Values)
                {
                    if (usbManager.HasPermission(device))
                    {
                        continue;
                    }
                    if (device.VendorId != AtolUsbVendorId && device.VendorId != FtdiUsbVendorId)
                    {
                        continue;
                    }

                    Intent intent = new Intent(ActionUsbPermission);
                    PendingIntent pendingIntent = PendingIntent.GetBroadcast(
                        context, 0, intent, PendingIntentFlags.Mutable);

                    AtolPrinterConfig.Log("USB",
                        "Запрос разрешения для USB устройства VID=0x" + device.VendorId.ToString("X4"));

                    usbManager.RequestPermission(device, pendingIntent);
                    // Системный диалог появится на экране планшета — кассир должен нажать "Разрешить".
                }
            }
            catch (System.Exception ex)
            {
                AtolPrinterConfig.Log("USB", "Не удалось запросить USB-разрешение: " + ex.Message);
                // Не блокируем: fptr.open() вернёт ошибку, если разрешения реально нет.
            }
        }

        // ------------------------------------------------------------------------
        // Диалоги (замена MessageBoxHelper) — всегда на главном потоке.
        // ------------------------------------------------------------------------
        private static System.Threading.Tasks.Task ShowAlertAsync(string title, string message)
        {
            AtolPrinterConfig.Log("Alert", title + ": " + message);
            return Microsoft.Maui.Essentials.MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var page = Microsoft.Maui.Controls.Application.Current?.MainPage;
                if (page != null)
                {
                    await page.DisplayAlert(title, message, "ОК");
                }
            });
        }

        private static System.Threading.Tasks.Task<bool> ShowConfirmAsync(string title, string message)
        {
            return Microsoft.Maui.Essentials.MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var page = Microsoft.Maui.Controls.Application.Current?.MainPage;
                if (page == null)
                {
                    return false;
                }
                return await page.DisplayAlert(title, message, "Да", "Нет");
            });
        }

        // ------------------------------------------------------------------------
        // ОСНОВНОЙ МЕТОД: печать чека продажи / возврата.
        // Порт десктопного print_sell_2_or_return_sell (без ЧЗ-валидации и рекламы).
        // Для QR-чеков: ReceiptType = 0, безналичная сумма (включая СБП) -> SumElectron.
        // ------------------------------------------------------------------------
        public static System.Threading.Tasks.Task<FiscalPrintResult> PrintSellOrReturnAsync(FiscalReceiptRequest request)
        {
            return System.Threading.Tasks.Task.Run(async () =>
            {
                if (request == null || string.IsNullOrWhiteSpace(request.DocKey))
                {
                    return FiscalPrintResult.Fail(-1, "Не заполнены данные чека (DocKey)");
                }
                if (request.Positions == null || request.Positions.Count == 0)
                {
                    return FiscalPrintResult.Fail(-1, "В чеке нет позиций для печати");
                }

                string docKey = request.DocKey;
                bool alreadyPrinting;
                lock (_currentlyPrintingDocs)
                {
                    alreadyPrinting = _currentlyPrintingDocs.Contains(docKey);
                    if (!alreadyPrinting)
                    {
                        _currentlyPrintingDocs.Add(docKey);
                    }
                }

                if (alreadyPrinting)
                {
                    AtolPrinterConfig.Log("PrintGuard", "Попытка повторной печати документа " + docKey + "! Заблокировано.");
                    await ShowAlertAsync("Печать чека",
                        "Этот чек уже отправлен на фискальный регистратор. Подождите завершения печати.");
                    return FiscalPrintResult.Fail(-2, "Чек уже в печати");
                }

                try
                {
                    // TODO (паритет с десктопом, ЗАЩИТА 2): проверить флаг its_print в БД
                    // (колонка в checks_header) и вернуть Ok, если чек уже был напечатан.

                    return await System.Threading.Tasks.Task.Run(async () =>
                        await PrintReceiptCore(request, docKey));
                }
                finally
                {
                    lock (_currentlyPrintingDocs)
                    {
                        _currentlyPrintingDocs.Remove(docKey);
                    }
                }
            });
        }

        private static async System.Threading.Tasks.Task<FiscalPrintResult> PrintReceiptCore(
            FiscalReceiptRequest request, string docKey)
        {
            bool error = false;

            IFptr fptr = GetFptr();
            if (!EnsureOpen(fptr, out int openCode, out string openDesc))
            {
                await ShowAlertAsync("Ошибка связи с ФР",
                    "Не удалось подключиться к фискальному регистратору.\nОшибка " + openCode + ": " + openDesc);
                return FiscalPrintResult.Fail(openCode, openDesc);
            }

            // --- Оператор (теги 1021/1203), паритет с десктопом ---
            fptr.setParam(1021, AtolPrinterConfig.OperatorName);
            fptr.setParam(1203, AtolPrinterConfig.OperatorInn);
            fptr.operatorLogin();

            // --- Тип чека ---
            if (request.ReceiptType == 0)
            {
                fptr.setParam(IFptr.LIBFPTR_PARAM_RECEIPT_TYPE, IFptr.LIBFPTR_RT_SELL);
            }
            else if (request.ReceiptType == 1)
            {
                fptr.setParam(IFptr.LIBFPTR_PARAM_RECEIPT_TYPE, IFptr.LIBFPTR_RT_SELL_RETURN);
            }
            else
            {
                return FiscalPrintResult.Fail(-1, "Тип чека " + request.ReceiptType + " на планшете не поддерживается (0=продажа, 1=возврат)");
            }

            // --- Телефон/email покупателя (тег 1008) ---
            if (!string.IsNullOrWhiteSpace(request.ClientContact))
            {
                fptr.setParam(1008, request.ClientContact.Trim());
                fptr.setParam(IFptr.LIBFPTR_PARAM_RECEIPT_ELECTRONICALLY, true);
            }

            // --- Система налогообложения (тег 1055), паритет с десктопом ---
            if (AtolPrinterConfig.SystemTaxation == 1)
            {
                fptr.setParam(1055, IFptr.LIBFPTR_TT_OSN);
            }
            else if (AtolPrinterConfig.SystemTaxation == 2)
            {
                fptr.setParam(1055, IFptr.LIBFPTR_TT_USN_INCOME_OUTCOME);
            }
            else
            {
                fptr.setParam(1055, IFptr.LIBFPTR_TT_USN_INCOME);
            }

            // --- Открытие чека ---
            if (fptr.openReceipt() != 0)
            {
                int errorCode = fptr.errorCode();
                string errorDesc = fptr.errorDescription();
                AtolPrinterConfig.Log("PrintReceipt",
                    "Ошибка открытия чека " + docKey + ": " + errorCode + " " + errorDesc);

                await ShowAlertAsync("Ошибка открытия чека",
                    "Ошибка при открытии чека.\nОшибка " + errorCode + ": " + errorDesc);

                if (errorCode == 82)
                {
                    fptr.cancelReceipt();
                    await ShowAlertAsync("Ошибка при печати чека", "Попробуйте распечатать чек еще раз");
                }
                else if (errorCode == 44)
                {
                    try { fptr.cancelReceipt(); } catch { }
                    await ShowAlertAsync("Нет бумаги",
                        "В фискальном регистраторе НЕТ БУМАГИ.\nВставьте бумагу и попробуйте снова.");
                }
                return FiscalPrintResult.Fail(errorCode, errorDesc);
            }

            // --- Позиции чека, паритет с десктопом ---
            foreach (FiscalPosition p in request.Positions)
            {
                string code = (p.Code ?? "").Trim();
                string tovar = (p.Tovar ?? "").Trim();
                string mark = (p.Mark ?? "").Trim().Replace("vasya2021", "'");

                if (!p.IsExcise)
                {
                    if (mark.Length <= 14)
                    {
                        // Обычный товар без маркировки: признак способа расчёта = 32
                        fptr.setParam(1212, 32);
                    }
                    else
                    {
                        // Маркированный товар
                        // TODO: ЧЗ-валидация (cdn_check) на планшете пока не портирована,
                        // поэтому 1260 не формируется, результат онлайн-проверки = 0.
                        fptr.setParam(IFptr.LIBFPTR_PARAM_MARKING_CODE, mark);
                        fptr.setParam(IFptr.LIBFPTR_PARAM_MARKING_CODE_TYPE, IFptr.LIBFPTR_MCT12_AUTO);
                        if (request.ReceiptType == 0)
                        {
                            fptr.setParam(IFptr.LIBFPTR_PARAM_MARKING_CODE_STATUS, IFptr.LIBFPTR_MES_PIECE_SOLD);
                        }
                        else
                        {
                            fptr.setParam(IFptr.LIBFPTR_PARAM_MARKING_CODE_STATUS, IFptr.LIBFPTR_MES_PIECE_RETURN);
                        }
                        fptr.setParam(IFptr.LIBFPTR_PARAM_COMMODITY_NAME, code + " " + tovar);
                        fptr.setParam(IFptr.LIBFPTR_PARAM_MARKING_CODE_ONLINE_VALIDATION_RESULT, 0);
                        fptr.setParam(IFptr.LIBFPTR_PARAM_MARKING_PROCESSING_MODE, 0);
                    }
                }
                else
                {
                    // Акцизный товар
                    fptr.setParam(1212, 2);
                }

                fptr.setParam(IFptr.LIBFPTR_PARAM_COMMODITY_NAME, code + " " + tovar);
                fptr.setParam(IFptr.LIBFPTR_PARAM_PRICE, p.PriceAtDiscount.ToString().Replace(",", "."));
                fptr.setParam(IFptr.LIBFPTR_PARAM_MEASUREMENT_UNIT,
                    p.IsWeighted ? IFptr.LIBFPTR_IU_KILOGRAM : IFptr.LIBFPTR_IU_PIECE);
                fptr.setParam(IFptr.LIBFPTR_PARAM_QUANTITY, p.Quantity.ToString().Replace(",", "."));

                int? taxType = MapVat(p, request.ReceiptType);
                if (taxType == int.MinValue)
                {
                    await ShowAlertAsync("Проверка ставки НДС", "Неизвестная ставка НДС у товара " + code + " " + tovar);
                    fptr.cancelReceipt();
                    return FiscalPrintResult.Fail(-1, "Неизвестная ставка НДС: " + p.StavkaNds);
                }
                if (taxType.HasValue)
                {
                    fptr.setParam(IFptr.LIBFPTR_PARAM_TAX_TYPE, taxType.Value);
                }

                if (request.ReceiptType == 0)
                {
                    // Предмет расчёта: сертификат = 3, товар = 4
                    fptr.setParam(1214, p.IsSertificate ? 3 : 4);
                }

                fptr.registration();
                if (fptr.errorCode() > 0)
                {
                    string msg = "При печати позиции " + code + " " + tovar + " произошли ошибки\r\n" +
                                 "Код ошибки " + fptr.errorCode() + "\r\n" + fptr.errorDescription();
                    AtolPrinterConfig.Log("PrintReceipt", msg);
                    await ShowAlertAsync("Ошибка при печати", msg);
                    fptr.cancelReceipt();
                    return FiscalPrintResult.Fail(fptr.errorCode(), fptr.errorDescription());
                }
            }

            // --- Итог чека (сумма позиций, паритет с calculation_of_the_sum_of_the_document) ---
            double total = System.Math.Round(
                SumPositions(request.Positions), 2);
            fptr.setParam(IFptr.LIBFPTR_PARAM_SUM, total);
            fptr.receiptTotal();

            // --- Оплаты, паритет с десктопом ---
            // QR/СБП входит в безналичную сумму => LIBFPTR_PT_ELECTRONICALLY
            if (request.SumCash > 0)
            {
                fptr.setParam(IFptr.LIBFPTR_PARAM_PAYMENT_TYPE, IFptr.LIBFPTR_PT_CASH);
                fptr.setParam(IFptr.LIBFPTR_PARAM_PAYMENT_SUM, request.SumCash);
                fptr.payment();
            }
            if (request.SumElectron > 0)
            {
                fptr.setParam(IFptr.LIBFPTR_PARAM_PAYMENT_TYPE, IFptr.LIBFPTR_PT_ELECTRONICALLY);
                fptr.setParam(IFptr.LIBFPTR_PARAM_PAYMENT_SUM, request.SumElectron);
                fptr.payment();
            }
            if (request.SumPrepaid > 0)
            {
                fptr.setParam(IFptr.LIBFPTR_PARAM_PAYMENT_TYPE, IFptr.LIBFPTR_PT_PREPAID);
                fptr.setParam(IFptr.LIBFPTR_PARAM_PAYMENT_SUM, request.SumPrepaid);
                fptr.payment();
            }

            // --- Отложенные строки чека ---
            if (request.ReceiptType == 0 && !string.IsNullOrEmpty(request.DiscountNote))
            {
                fptr.setParam(IFptr.LIBFPTR_PARAM_TEXT, request.DiscountNote);
                fptr.setParam(IFptr.LIBFPTR_PARAM_DEFER, IFptr.LIBFPTR_DEFER_POST);
                fptr.setParam(IFptr.LIBFPTR_PARAM_ALIGNMENT, IFptr.LIBFPTR_ALIGNMENT_CENTER);
                fptr.printText();
            }

            string shopLine = AtolPrinterConfig.NickShop + "-" + AtolPrinterConfig.CashDeskNumber + "-" + docKey;
            fptr.setParam(IFptr.LIBFPTR_PARAM_TEXT, shopLine);
            fptr.setParam(IFptr.LIBFPTR_PARAM_DEFER, IFptr.LIBFPTR_DEFER_POST);
            fptr.setParam(IFptr.LIBFPTR_PARAM_ALIGNMENT, IFptr.LIBFPTR_ALIGNMENT_LEFT);
            fptr.printText();

            // TODO: реклама (print_fiscal_advertisement) — на планшете не портирована.

            // --- Пользовательский реквизит TLV 1084 "NumCheckShop" (паритет) ---
            fptr.setParam(1085, "NumCheckShop");
            fptr.setParam(1086, shopLine);
            fptr.utilFormTlv();
            byte[] userAttribute = fptr.getParamByteArray(IFptr.LIBFPTR_PARAM_TAG_VALUE);
            fptr.setNonPrintableParam(1084, userAttribute);

            if (fptr.errorCode() > 0)
            {
                string msg = "При печати чека произошли ошибки\r\nКод ошибки " + fptr.errorCode() +
                             "\r\n" + fptr.errorDescription();
                AtolPrinterConfig.Log("PrintReceipt", msg);
                await ShowAlertAsync("Ошибка при печати", msg);
                fptr.cancelReceipt();
                return FiscalPrintResult.Fail(fptr.errorCode(), fptr.errorDescription());
            }

            fptr.closeReceipt();

            // --- Ожидание закрытия документа (паритет с десктопом) ---
            while (fptr.checkDocumentClosed() < 0)
            {
                await ShowAlertAsync("Ошибка при печати чека",
                    fptr.errorCode() + " " + fptr.errorDescription());
                if (!await ShowConfirmAsync("Ошибка при печати чека", "Продолжать попытки печати чека?"))
                {
                    error = true;
                    break;
                }
            }

            if (!error && !fptr.getParamBool(IFptr.LIBFPTR_PARAM_DOCUMENT_CLOSED))
            {
                fptr.cancelReceipt();
                string msg = "Не удалось напечатать документ (Ошибка \"" + fptr.errorDescription() +
                             "\"). Устраните неполадку и повторите.";
                AtolPrinterConfig.Log("PrintReceipt", msg);
                await ShowAlertAsync("Ошибка при печати чека", msg);
                return FiscalPrintResult.Fail(fptr.errorCode(), fptr.errorDescription());
            }

            // --- Допечатка при закончившейся бумаге (паритет) ---
            if (!error && !fptr.getParamBool(IFptr.LIBFPTR_PARAM_DOCUMENT_PRINTED))
            {
                while (fptr.continuePrint() < 0)
                {
                    await ShowAlertAsync("Ошибка при печати чека",
                        "Не удалось напечатать документ (Ошибка \"" + fptr.errorDescription() +
                        "\"). Устраните неполадку и повторите.");
                    fptr.setParam(IFptr.LIBFPTR_PARAM_DATA_TYPE, IFptr.LIBFPTR_DT_SHORT_STATUS);
                    fptr.queryData();
                    if (!fptr.getParamBool(IFptr.LIBFPTR_PARAM_RECEIPT_PAPER_PRESENT))
                    {
                        await ShowAlertAsync("Ошибка при печати чека", "В ФР закончилась бумага.");
                    }
                }
            }

            // TODO (паритет): если печать успешна — установить флаг its_print для чека в БД.

            AtolPrinterConfig.Log("PrintReceipt", "Чек " + docKey + " напечатан успешно");
            return FiscalPrintResult.Ok();
        }

        private static double SumPositions(System.Collections.Generic.List<FiscalPosition> positions)
        {
            double sum = 0;
            foreach (FiscalPosition p in positions)
            {
                sum += p.PriceAtDiscount * p.Quantity;
            }
            return sum;
        }

        // Соответствие ставки НДС (паритет с десктопом, включая правило 2026 года).
        // Возвращает int.MinValue при неизвестной ставке ОСН, null — если тег не ставится (УСН).
        private static int? MapVat(FiscalPosition p, int receiptType)
        {
            bool cert = p.IsSertificate;
            int year = System.DateTime.Now.Year;

            if (AtolPrinterConfig.SystemTaxation == 1)
            {
                int stavka = p.StavkaNds;
                if (stavka == 0) return IFptr.LIBFPTR_TAX_VAT0;
                if (stavka == 10) return IFptr.LIBFPTR_TAX_VAT10;
                if (stavka == 18) return IFptr.LIBFPTR_TAX_VAT20;
                if (stavka == 20) return year >= 2026 ? IFptr.LIBFPTR_TAX_VAT22 : IFptr.LIBFPTR_TAX_VAT20;
                if (stavka == 22)
                {
                    return cert ? (year >= 2026 ? IFptr.LIBFPTR_TAX_VAT122 : IFptr.LIBFPTR_TAX_VAT120)
                                : IFptr.LIBFPTR_TAX_VAT22;
                }
                return int.MinValue;
            }
            else
            {
                int ndsIp = AtolPrinterConfig.NdsIp;
                if (ndsIp == 0) return IFptr.LIBFPTR_TAX_NO;
                if (ndsIp == 5) return cert ? IFptr.LIBFPTR_TAX_VAT105 : IFptr.LIBFPTR_TAX_VAT5;
                if (ndsIp == 7) return cert ? IFptr.LIBFPTR_TAX_VAT107 : IFptr.LIBFPTR_TAX_VAT7;
                return null;
            }
        }

        // ======================================================================
        // Служебные операции (порты десктопных методов).
        // ======================================================================

        /// <summary>X-отчёт (паритет reportX).</summary>
        public static System.Threading.Tasks.Task<FiscalPrintResult> PrintXReportAsync()
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                IFptr fptr = GetFptr();
                if (!EnsureOpen(fptr, out int code, out string desc))
                {
                    return FiscalPrintResult.Fail(code, desc);
                }
                fptr.setParam(1021, AtolPrinterConfig.OperatorName);
                fptr.setParam(1203, AtolPrinterConfig.OperatorInn);
                fptr.operatorLogin();
                fptr.setParam(IFptr.LIBFPTR_PARAM_REPORT_TYPE, IFptr.LIBFPTR_RT_X);
                if (fptr.report() != 0)
                {
                    ShowAlertAsync("Ошибка при X отчете",
                        string.Format("Ошибка при X отчете.\nОшибка {0}: {1}", fptr.errorCode(), fptr.errorDescription())).Wait();
                    return FiscalPrintResult.Fail(fptr.errorCode(), fptr.errorDescription());
                }
                return FiscalPrintResult.Ok();
            });
        }

        /// <summary>Закрытие смены, Z-отчёт (паритет reportZ).</summary>
        public static System.Threading.Tasks.Task<FiscalPrintResult> PrintZReportAsync()
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                IFptr fptr = GetFptr();
                if (!EnsureOpen(fptr, out int code, out string desc))
                {
                    return FiscalPrintResult.Fail(code, desc);
                }
                fptr.setParam(1021, AtolPrinterConfig.OperatorName);
                fptr.setParam(1203, AtolPrinterConfig.OperatorInn);
                fptr.operatorLogin();
                fptr.setParam(IFptr.LIBFPTR_PARAM_REPORT_TYPE, IFptr.LIBFPTR_RT_CLOSE_SHIFT);
                if (fptr.report() != 0)
                {
                    ShowAlertAsync("Ошибка закрытия смены",
                        string.Format("Ошибка при закрытии смены.\nОшибка {0}: {1}", fptr.errorCode(), fptr.errorDescription())).Wait();
                    return FiscalPrintResult.Fail(fptr.errorCode(), fptr.errorDescription());
                }
                return FiscalPrintResult.Ok();
            });
        }

        /// <summary>Повторная печать последнего документа (паритет print_last_document).</summary>
        public static System.Threading.Tasks.Task<FiscalPrintResult> PrintLastDocumentAsync()
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                IFptr fptr = GetFptr();
                if (!EnsureOpen(fptr, out int code, out string desc))
                {
                    return FiscalPrintResult.Fail(code, desc);
                }
                fptr.setParam(1021, AtolPrinterConfig.OperatorName);
                fptr.setParam(1203, AtolPrinterConfig.OperatorInn);
                fptr.operatorLogin();
                fptr.setParam(IFptr.LIBFPTR_PARAM_REPORT_TYPE, IFptr.LIBFPTR_RT_LAST_DOCUMENT);
                if (fptr.report() != 0)
                {
                    return FiscalPrintResult.Fail(fptr.errorCode(), fptr.errorDescription());
                }
                return FiscalPrintResult.Ok();
            });
        }

        /// <summary>Внесение наличных (паритет cashIncome).</summary>
        public static System.Threading.Tasks.Task<FiscalPrintResult> CashIncomeAsync(double sumCashIn)
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                IFptr fptr = GetFptr();
                if (!EnsureOpen(fptr, out int code, out string desc))
                {
                    return FiscalPrintResult.Fail(code, desc);
                }
                fptr.setParam(1021, AtolPrinterConfig.OperatorName);
                fptr.setParam(1203, AtolPrinterConfig.OperatorInn);
                fptr.operatorLogin();
                fptr.setParam(IFptr.LIBFPTR_PARAM_SUM, sumCashIn);
                if (fptr.cashIncome() != 0)
                {
                    ShowAlertAsync("Ошибка при внесении", fptr.errorDescription()).Wait();
                    return FiscalPrintResult.Fail(fptr.errorCode(), fptr.errorDescription());
                }
                return FiscalPrintResult.Ok();
            });
        }

        /// <summary>Инкассация / изъятие наличных (паритет cashOutcome).</summary>
        public static System.Threading.Tasks.Task<FiscalPrintResult> CashOutcomeAsync(double sumCashOut)
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                IFptr fptr = GetFptr();
                if (!EnsureOpen(fptr, out int code, out string desc))
                {
                    return FiscalPrintResult.Fail(code, desc);
                }
                fptr.setParam(1021, AtolPrinterConfig.OperatorName);
                fptr.setParam(1203, AtolPrinterConfig.OperatorInn);
                fptr.operatorLogin();
                fptr.setParam(IFptr.LIBFPTR_PARAM_SUM, sumCashOut);
                if (fptr.cashOutcome() != 0)
                {
                    ShowAlertAsync("Ошибка при инкасации", fptr.errorDescription()).Wait();
                    return FiscalPrintResult.Fail(fptr.errorCode(), fptr.errorDescription());
                }
                return FiscalPrintResult.Ok();
            });
        }

        /// <summary>Версия драйвера (паритет get_version).</summary>
        public static System.Threading.Tasks.Task<string> GetVersionAsync()
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                IFptr fptr = GetFptr();
                if (!EnsureOpen(fptr, out int code, out string desc))
                {
                    return "Ошибка " + code + ": " + desc;
                }
                return fptr.version();
            });
        }

        /// <summary>Состояние ресурсов ФН (паритет getFiscallInfo).</summary>
        public static System.Threading.Tasks.Task<string> GetFiscalInfoAsync()
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                IFptr fptr = GetFptr();
                if (!EnsureOpen(fptr, out int code, out string desc))
                {
                    return "Ошибка " + code + ": " + desc;
                }

                string fnInfo = "";
                fptr.setParam(IFptr.LIBFPTR_PARAM_FN_DATA_TYPE, IFptr.LIBFPTR_FNDT_VALIDITY_DAYS);
                fptr.setParam(IFptr.LIBFPTR_PARAM_DATE_TIME, System.DateTime.Now);
                fptr.fnQueryData();
                uint daysRemain = (uint)fptr.getParamInt(IFptr.LIBFPTR_PARAM_FN_DAYS_REMAIN);
                fnInfo = "Ресурс ФН " + daysRemain + " дней ";

                fptr.setParam(IFptr.LIBFPTR_PARAM_FN_DATA_TYPE, IFptr.LIBFPTR_FNDT_FN_INFO);
                fptr.fnQueryData();

                if (fptr.getParamBool(IFptr.LIBFPTR_PARAM_FN_NEED_REPLACEMENT))
                {
                    fnInfo += "\r\n" + "Требуется срочная замена ФН !!!";
                }
                if (fptr.getParamBool(IFptr.LIBFPTR_PARAM_FN_RESOURCE_EXHAUSTED))
                {
                    fnInfo += "\r\n" + "Исчерпан ресурс ФН !!!";
                }
                if (fptr.getParamBool(IFptr.LIBFPTR_PARAM_FN_MEMORY_OVERFLOW))
                {
                    fnInfo += "\r\n" + "Память ФН переполнена !!!";
                }
                if (fptr.getParamBool(IFptr.LIBFPTR_PARAM_FN_OFD_TIMEOUT))
                {
                    fnInfo += "\r\n" + "Превышено время ожидания ответа от ОФД !!!";
                }
                if (fptr.getParamBool(IFptr.LIBFPTR_PARAM_FN_CRITICAL_ERROR))
                {
                    fnInfo += "\r\n" + "Критическая ошибка ФН !!!";
                }
                return fnInfo;
            });
        }

        /// <summary>Состояние обмена с ОФД (паритет ofdExchangeStatus).</summary>
        public static System.Threading.Tasks.Task<string> GetOfdExchangeStatusAsync()
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                IFptr fptr = GetFptr();
                if (!EnsureOpen(fptr, out int code, out string desc))
                {
                    return "Ошибка " + code + ": " + desc;
                }

                fptr.setParam(IFptr.LIBFPTR_PARAM_FN_DATA_TYPE, IFptr.LIBFPTR_FNDT_OFD_EXCHANGE_STATUS);
                fptr.fnQueryData();

                uint unsentCount = (uint)fptr.getParamInt(IFptr.LIBFPTR_PARAM_DOCUMENTS_COUNT);
                if (unsentCount > 0)
                {
                    Java.Util.Date dt = fptr.getParamDateTime(IFptr.LIBFPTR_PARAM_DATE_TIME);
                    string when = dt != null
                        ? FromJavaDate(dt).ToString("yyyy-MM-dd HH:mm:ss")
                        : "";
                    return "В ОФД Не отправлено документов " + unsentCount + "\r\n начиная с даты " + when;
                }
                return "0";
            });
        }

        /// <summary>
        /// Состояние смены (паритет getShiftStatus). Возвращает LIBFPTR_SS_* (или -1 при ошибке).
        /// Если смена превысила 24 часа — предупреждает и автоматически закрывает смену (Z).
        /// </summary>
        public static System.Threading.Tasks.Task<int> GetShiftStatusAsync()
        {
            return System.Threading.Tasks.Task.Run(async () =>
            {
                IFptr fptr = GetFptr();
                if (!EnsureOpen(fptr, out int code, out string desc))
                {
                    return -1;
                }
                fptr.setParam(IFptr.LIBFPTR_PARAM_DATA_TYPE, IFptr.LIBFPTR_DT_SHIFT_STATE);
                fptr.queryData();
                int state = fptr.getParamInt(IFptr.LIBFPTR_PARAM_SHIFT_STATE);

                if (state == IFptr.LIBFPTR_SS_EXPIRED)
                {
                    await ShowAlertAsync("Проверка длительности смены",
                        " Период открытой смены превысил 24 часа!\r\nСмена будет закрыта автоматически!\r\n" +
                        "В ИТ-отдел звонить не надо, если хотите кому-нибудь позвонить., звоните в бухгалтерию");
                    await PrintZReportAsync();
                }
                return state;
            });
        }

        /// <summary>
        /// Проверка расхождения времени ПК и ФН (паритет validate_date_time_with_fn).
        /// При расхождении больше 15 минут время в ФР НЕ трогаем (возможен сбой времени на планшете).
        /// </summary>
        public static System.Threading.Tasks.Task<FiscalPrintResult> ValidateDateTimeWithFnAsync(int minutes)
        {
            const double MAX_ALLOWED_AUTOCORRECT_MINUTES = 15.0;

            return System.Threading.Tasks.Task.Run(async () =>
            {
                IFptr fptr = GetFptr();
                if (!EnsureOpen(fptr, out int code, out string desc))
                {
                    await ShowAlertAsync("Ошибка связи с ФР", "Не удалось подключиться к ФР:\r\n" + desc);
                    return FiscalPrintResult.Fail(code, desc);
                }

                fptr.setParam(IFptr.LIBFPTR_PARAM_DATA_TYPE, IFptr.LIBFPTR_DT_DATE_TIME);
                fptr.queryData();
                if (fptr.errorCode() != 0)
                {
                    await ShowAlertAsync("Проверка даты и времени в фн",
                        "При проверке даты и времени в ФН произошла ошибка\r\n" + fptr.errorDescription());
                    return FiscalPrintResult.Fail(fptr.errorCode(), fptr.errorDescription());
                }

                Java.Util.Date frDate = fptr.getParamDateTime(IFptr.LIBFPTR_PARAM_DATE_TIME);
                if (frDate == null)
                {
                    return FiscalPrintResult.Fail(-1, "ФН не вернул дату/время");
                }
                System.DateTime dateTime = FromJavaDate(frDate);

                double minutesFr = System.Math.Abs((dateTime - System.DateTime.Now).TotalMinutes);

                if (minutesFr > MAX_ALLOWED_AUTOCORRECT_MINUTES)
                {
                    AtolPrinterConfig.Log("ФР",
                        "КРИТИЧЕСКОЕ расхождение времени ФР и планшета: " + minutesFr.ToString("F1") +
                        " мин. Автокоррекция ОТМЕНЕНА");
                    await ShowAlertAsync("ВНИМАНИЕ! Расхождение времени",
                        "У ВАС ОТЛИЧАЕТСЯ ВРЕМЯ МЕЖДУ ПЛАНШЕТОМ И ФИСКАЛЬНЫМ РЕГИСТРАТОРОМ НА " +
                        minutesFr.ToString("F1") + " МИНУТ(Ы)!\r\n" +
                        "ВРЕМЯ АВТОМАТИЧЕСКИ НЕ ИСПРАВЛЕНО.\r\n" +
                        "ОБЯЗАТЕЛЬНО ОБРАТИТЕСЬ В ИТ-ОТДЕЛ.\r\n\r\n" +
                        "После отправки заявки вы можете продолжить работу в обычном режиме.");
                    return FiscalPrintResult.Ok();
                }

                if (minutesFr > minutes)
                {
                    fptr.setParam(IFptr.LIBFPTR_PARAM_DATA_TYPE, IFptr.LIBFPTR_DT_SHIFT_STATE);
                    fptr.queryData();
                    if (fptr.errorCode() != 0)
                    {
                        await ShowAlertAsync("Ошибка ФР",
                            "Не удалось получить состояние смены ФР:\r\n" + fptr.errorDescription());
                        return FiscalPrintResult.Fail(fptr.errorCode(), fptr.errorDescription());
                    }

                    int shiftState = fptr.getParamInt(IFptr.LIBFPTR_PARAM_SHIFT_STATE);

                    if (shiftState == IFptr.LIBFPTR_SS_CLOSED)
                    {
                        fptr.setParam(IFptr.LIBFPTR_PARAM_DATE_TIME, System.DateTime.Now);
                        fptr.writeDateTime();
                        if (fptr.errorCode() == 0)
                        {
                            AtolPrinterConfig.Log("ФР",
                                "Время на ФР автоматически скорректировано. Рассинхрон был: " +
                                minutesFr.ToString("F1") + " мин.");
                            return FiscalPrintResult.Ok();
                        }
                        AtolPrinterConfig.Log("ФР ОШИБКА",
                            "Ошибка автокорректировки времени ФР: " + fptr.errorDescription());
                        await ShowAlertAsync("Ошибка ФР",
                            "Не удалось автоматически скорректировать время на ФР:\r\n" + fptr.errorDescription());
                        return FiscalPrintResult.Fail(fptr.errorCode(), fptr.errorDescription());
                    }

                    AtolPrinterConfig.Log("ФР",
                        "Расхождение времени ФР и планшета: " + minutesFr.ToString("F1") +
                        " мин. Смена открыта, автокоррекция невозможна.");
                    await ShowAlertAsync("Расхождение времени ФР",
                        "Время в ФР отличается на " + minutesFr.ToString("F1") + " минут.\r\n\r\n" +
                        "Закройте смену, перезайдите в кассовую программу и, если необходимо, снова откройте смену.\r\n\r\n" +
                        "(Время скорректируется автоматически при закрытой смене)");
                    return FiscalPrintResult.Ok();
                }

                return FiscalPrintResult.Ok();
            });
        }

        private static System.DateTime FromJavaDate(Java.Util.Date date)
        {
            return System.DateTimeOffset.FromUnixTimeMilliseconds(date.Time).LocalDateTime;
        }
    }
}
