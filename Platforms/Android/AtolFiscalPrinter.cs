// ============================================================================
// AtolFiscalPrinter.cs — порт десктопного PrintingUsingLibraries.cs (Cash8Avalon)
// на планшет CashPlace (.NET MAUI, Android, ФР АТОЛ, ДТО-10).
//
// ПЕЧАТЬ ЧЕКОВ, ОПЛАЧЕННЫХ ПО QR (СБП): безналичная часть чека передаётся ФР
// как электронная оплата (LIBFPTR_PT_ELECTRONICALLY) — полный паритет с десктопом.
//
// Файл должен лежать в Platforms/Android/ — компилируется только под Android TFM.
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
//  2. ВСЕ члены биндинга — PascalCase (как их сгенерировал биндинг):
//       методы:    setParam -> SetParam, errorCode -> ErrorCode, queryData -> QueryData,
//                  cancelReceipt -> CancelReceipt, operatorLogin -> OperatorLogin, ...
//       константы: LIBFPTR_PARAM_SUM -> LibfptrParamSum,
//                  LIBFPTR_PT_ELECTRONICALLY -> LibfptrPtElectronically, ...
//     Ниже использованы именно сгенерированные имена.
//  3. Класс драйвера — FptrImpl (переименован Metadata.xml), константы и методы — IFptr.
//  4. Все вызовы ДТО-10 блокирующие => фискальный сценарий выполняется в Task.Run.
//  5. MessageBoxHelper -> системные DisplayAlert на главном потоке.
//  6. Даты в ФР передаются как Java.Util.Date:
//     SetParam(IFptr.LibfptrParamDateTime, ToJavaDate(DateTime.Now)) — биндинг
//     не принимает System.DateTime (метод setParam(int, Date)).
//  7. Реклама из Postgres и проверка Честного знака на планшете пока НЕ портированы
//     (помечены TODO). Код маркировки передаётся в ФР как есть.
//  8. Флаг its_print в БД планшета пока не портирован: защита от повторной печати —
//     HashSet в памяти (от двойного клика). TODO: добавить колонку its_print в checks_header.
//
// АНТИ-ОШИБКА-4 («ПОРТ НЕДОСТУПЕН»), обновление:
//  - ResetConnection() — полный сброс драйвера (close + null), можно вызывать вручную.
//  - EnsureOpen(): если Open() не удался, драйвер ПЕРЕСОЗДАЁТСЯ и попытка
//    повторяется на свежем экземпляре. Лечит "зависший" USB-стек драйвера
//    после переподключения ФР по USB или перезагрузки планшета.
//  - TestConnectionAsync(): в тексте ошибки появилась подсказка про сервисную
//    утилиту АТОЛ. ВАЖНО: Android отдаёт USB-порт ФР только ОДНОМУ приложению.
//    Если сервисная утилита подключена к ФР — она удерживает порт, и кассовое
//    приложение получает ошибку 4, даже когда разрешение на USB выдано.
//    Порядок работы: поработали утилитой -> ПОЛНОСТЬЮ её остановили ->
//    только потом работает касса (и наоборот).
//  - TestConnectionAsync() показывает ДИАГНОСТИКУ USB: список всех USB-устройств
//    планшета (VID/PID) и есть ли у приложения разрешение на каждое из них.
//  - В список VID ФР добавлен 0x2912 (10514) — тот же, что в device_filter.xml
//    (прежде в коде запроса разрешения стоял только 0x2E83, и запрос на реальный
//    ФР не срабатывал).
//  - СБОРКА 4: исправлен запрос USB-разрешения для Android 14 — для Mutable
//    PendingIntent теперь задаётся SetPackage (без него GetBroadcast бросает
//    исключение и системный диалог «Разрешить» вообще не появляется, ошибка
//    проглатывалась молча). Отчёт о запросе печатается в ДИАГНОСТИКЕ USB.
//  - СБОРКА 5: (1) контекст драйвера возвращён к рабочему варианту
//    CurrentActivity ?? Application.Context (с Application.Context связь
//    не открывалась — ошибка 4); (2) EnsureOpen перебирает стратегии
//    подключения на ТОМ ЖЕ экземпляре драйвера (пересоздавать нельзя —
//    вызывающий код держит ссылку): автопоиск USB -> повтор -> явное имя
//    устройства (usbDeviceName / deviceName); удачная стратегия запоминается;
//    (3) при ошибке 4 на экране — ПРЯМАЯ ПРОВЕРКА ПОРТА (OpenDevice +
//    ClaimInterface мимо драйвера): сразу видно, занят ли порт кем-то чужим
//    или проблема в настройках драйвера; (4) DestroyFptrInstance закрывает
//    "полуоткрытый" USB-коннект; (5) в диагностике показан ПУТЬ устройства
//    (/dev/bus/usb/BBB/DDD).
//  - СБОРКА 6: зонд порта в тесте связи выполняется ДВАЖДЫ — ДО первой попытки
//    драйвера открыть связь (когда связь ещё не открыта) и ПОСЛЕ неудачных
//    попыток. Сравнение двух зондов отличает: чужой захват порта (занят уже ДО),
//    "полуоткрытую" утечку собственного драйвера (ДО свободен, ПОСЛЕ занят) и
//    проблему в настройках/самом ФР (оба свободны, а ошибка 4 остаётся).
//  - СБОРКА 7: по зондам СБОРКИ 6 порт СВОБОДЕН (OpenDevice + захват интерфейса
//    ОК и ДО, и ПОСЛЕ), а драйвер всё равно даёт ошибку 4 по всем стратегиям
//    => "залипает" СОСТОЯНИЕ ЭКЗЕМПЛЯРА драйвера после первой неудачи.
//    Каждая повторная попытка снова выполняется на СВЕЖЕМ экземпляре (так
//    работало вчера), EnsureOpen возвращает живой экземпляр через out —
//    EnsureOpen(out IFptr fptr, ...); Close() в DestroyFptrInstance зовётся
//    ВСЕГДА; в лог пишется контекст создания драйвера (Activity/Application).
//  - СБОРКА 8: кнопка «НАСТРОЙКИ АТОЛ» на странице ФР открывает РОДНОЕ окно
//    настроек драйвера (SettingsActivity из той же libfptr10.aar — механизм
//    официального примера АТОЛ). Окно возвращает JSON, сгенерированный самим
//    АТОЛ; драйвер пересоздаётся и открывается этим JSON. Если и родные
//    настройки не открывают порт — проблема не в настройках кассы (готовые
//    данные для поддержки АТОЛ). Класс SettingsActivity ищется отражением
//    (Class.forName) по вариантам пакета; имя extra-ключа DEVICE_SETTINGS
//    тоже читается отражением — биндинг не обязан его экспортировать.//  - СБОРКА 9: «приложение сворачивается» при НАСТРОЙКАХ АТОЛ = падение
//    ЧУЖОЙ активности, наш try/catch его не видит. Ответ: (а) в манифест
//    добавлено объявление SettingsActivity с темой AppCompat (без неё
//    активность AppCompat падает со штатным IllegalStateException);
//    (б) prefill-JSON родному окну больше не передаётся; (в) перехватчик
//    падений: перед смертью процесса исключение пишется в файл, при
//    следующем тесте связи показывается на экране вместо молчания.//  - СБОРКА 10: РАЗГАДКА ошибки 4 — рукописный {"type":"usb"} на этой
//    версии драйвера порт не открывает; рабочий JSON генерирует родное
//    окно АТОЛ (полный объект с "Port":"1", "UsbDevicePath":"auto", …).
//    Связь этим JSON ОТКРЫЛАСЬ (подтверждено на планшете). Рабочий JSON
//    сохраняется в файл и грузится при старте: связь открывается сразу
//    после перезапуска; окно настроек открывается с prefill из файла.
//    Кнопка сохранения родного окна — стрелка <- в его шапке.
//  - СБОРКА 11 (ИСПРАВЛЕНИЕ СБОРКИ 10): пользователь подтвердил — настройки
//    «не сохраняются между запусками». Причина: SaveSettingsToFile стоял
//    только внутри EnsureOpen, а после успешного открытия родными настройками
//    связь была УЖЕ открыта — EnsureOpen выходил сразу (IsOpened == true),
//    до записи файла дело не доходило, файл не создавался НИКОГДА, хотя
//    отчёт писал «настройки СОХРАНЕНЫ В ФАЙЛ». Теперь JSON, возвращённый
//    родным окном, пишется в файл СРАЗУ при получении (до попытки Open) —
//    выбор «USB» переживает перезапуск кассы. Тест связи показывает
//    содержимое файла сохранённых настроек и ошибки записи (если были).
// ============================================================================

using Android.App;
using Android.Content;
using Android.Hardware.Usb;
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
    // СБОРКА 8: мост к РОДНОМУ окну настроек драйвера АТОЛ.
    // SettingsActivity лежит ВНУТРИ libfptr10.aar; официальный пример АТОЛ:
    //   intent.putExtra(SettingsActivity.DEVICE_SETTINGS, fptr.getSettings());
    //   startActivityForResult(intent, REQUEST); -> в onActivityResult:
    //   setSettings(data.getStringExtra(SettingsActivity.DEVICE_SETTINGS));
    // Java-класс ищется отражением (Class.forName) по вариантам пакета —
    // биндинг мог сгенерировать другое имя, а Java-класс в .aar есть всегда.
    // Имя extra-ключа читается отражением (статическое поле DEVICE_SETTINGS).
    // Активность попадает в манифест автоматически по атрибуту [Activity].
    // ==========================================================================
    [Android.App.Activity(Label = "ATOL settings bridge",
                          Theme = "@android:style/Theme.Translucent.NoTitleBar",
                          Exported = false)]
    public class AtolSettingsBridgeActivity : Android.App.Activity
    {
        public const int RequestCode = 41001;
        private const string ExtraCurrentSettings = "cashplace.current.settings";
        private const string FallbackSettingsKey = "ru.atol.drivers10.fptr.settings.DEVICE_SETTINGS";
        // --------------------------------------------------------------------
        // СБОРКА 9: журнал падений. Если родное окно настроек (или что-то ещё)
        // падает с необработанным исключением, процесс молча умирает —
        // пользователь видит «приложение свернулось». ПЕРЕД смертью процесса
        // пишем исключение в файл; следующий тест связи (или повторное нажатие
        // «НАСТРОЙКИ АТОЛ») покажет этот текст на экране.
        // --------------------------------------------------------------------
        public static readonly string CrashFilePath =
            System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "cashplace_last_crash.txt");

        private sealed class CrashCapturingHandler : Java.Lang.Object,
            Java.Lang.Thread.IUncaughtExceptionHandler
        {
            private readonly Java.Lang.Thread.IUncaughtExceptionHandler _previous;

            public CrashCapturingHandler(Java.Lang.Thread.IUncaughtExceptionHandler previous)
            {
                _previous = previous;
            }

            public void UncaughtException(Java.Lang.Thread thread, Java.Lang.Throwable ex)
            {
                WriteCrashFile("JAVA", ex == null ? "null" : ex.ToString());
                var previous = _previous;
                if (previous != null)
                {
                    // Отдаём падение штатному обработчику: приложение закроется
                    // как обычно, но текст исключения уже сохранён в файл.
                    previous.UncaughtException(thread, ex);
                }
            }
        }

        public static void InstallCrashCapture()
        {
            try
            {
                var previous = Java.Lang.Thread.DefaultUncaughtExceptionHandler;
                if (!(previous is CrashCapturingHandler))
                {
                    Java.Lang.Thread.DefaultUncaughtExceptionHandler =
                        new CrashCapturingHandler(previous);
                }
            }
            catch { }
            try
            {
                Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser -= OnManagedUnhandled;
                Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += OnManagedUnhandled;
            }
            catch { }
        }

        private static void OnManagedUnhandled(object sender, Android.Runtime.RaiseThrowableEventArgs e)
        {
            WriteCrashFile("MANAGED", e == null || e.Exception == null ? "null" : e.Exception.ToString());
        }

        public static void WriteCrashFile(string kind, string text)
        {
            try
            {
                string stamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                System.IO.File.AppendAllText(CrashFilePath,
                    "ПАДЕНИЕ (" + kind + ") " + stamp + "\n" + (text ?? "null") + "\n\n");
            }
            catch { }
        }

        /// <summary>Прочитать журнал падений для показа на экране и очистить его.</summary>
        public static string ReadAndClearCrashReport()
        {
            try
            {
                if (!System.IO.File.Exists(CrashFilePath))
                {
                    return "";
                }
                string text = System.IO.File.ReadAllText(CrashFilePath).TrimEnd();
                try { System.IO.File.Delete(CrashFilePath); } catch { }
                if (text.Length > 2400)
                {
                    text = text.Substring(0, 200) + "\n…(середина журнала пропущена)…\n" +
                           text.Substring(text.Length - 2000);
                }
                return text;
            }
            catch
            {
                return "";
            }
        }

        private static System.Threading.Tasks.TaskCompletionSource<string> _tcs;
        private static string _settingsKey = FallbackSettingsKey;
        private static string _resolvedClass = "";
        private static string _launchError = "";

        /// <summary>Что произошло при последнем запуске (попадает в отчёт на экране).</summary>
        public static string LastLaunchReport
        {
            get
            {
                return "класс SettingsActivity: " + (_resolvedClass == "" ? "НЕ найден" : _resolvedClass) +
                       (_launchError == "" ? "" : "; ошибка: " + _launchError);
            }
        }

        /// <summary>
        /// Открыть родное окно настроек АТОЛ. Вернёт JSON-настройки либо null
        /// (отмена/закрыто/класс не найден — подробности в LastLaunchReport).
        /// </summary>
        public static System.Threading.Tasks.Task<string> LaunchAsync(Context caller, string currentSettingsJson)
        {
            _launchError = "";
            var tcs = new System.Threading.Tasks.TaskCompletionSource<string>(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            _tcs = tcs;

            void Start()
            {
                try
                {
                    var bridgeIntent = new Intent(caller, typeof(AtolSettingsBridgeActivity));
                    if (!string.IsNullOrEmpty(currentSettingsJson))
                    {
                        // СБОРКА 10: prefill возвращён (только JSON, порождённый АТОЛ).
                        bridgeIntent.PutExtra(ExtraCurrentSettings, currentSettingsJson);
                    }
                    if (!(caller is Android.App.Activity))
                    {
                        // Запуск из контекста приложения возможен только с NEW_TASK.
                        bridgeIntent.SetFlags(Android.Content.ActivityFlags.NewTask);
                    }
                    caller.StartActivity(bridgeIntent);
                }
                catch (System.Exception ex)
                {
                    _launchError = ex.GetType().Name + ": " + ex.Message;
                    _tcs.TrySetResult(null);
                }
            }

            if (caller is Activity act)
            {
                act.RunOnUiThread(Start);
            }
            else
            {
                Start();
            }
            return tcs.Task;
        }

        protected override void OnCreate(Android.OS.Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // СБОРКА 9: до запуска ЧУЖОЙ активности ставим перехватчик падений.
            // Если окно АТОЛ упадёт при старте — процесс умолкнет («свернулось»),
            // но исключение будет записано в файл и показано при следующем
            // тесте связи или повторном нажатии «НАСТРОЙКИ АТОЛ».
            InstallCrashCapture();

            if (savedInstanceState != null)
            {
                // Пересоздание (поворот экрана): окно уже открыто, результат
                // всё равно придёт в OnActivityResult — повторно не запускаем.
                return;
            }

            try
            {
                // 1) Ищем SettingsActivity внутри libfptr10.aar по вариантам пакета.
                string[] candidates = new string[]
                {
                    "ru.atol.drivers10.fptr.settings.SettingsActivity",
                    "ru.atol.drivers10.fptr.SettingsActivity"
                };
                Java.Lang.Class cls = null;
                foreach (string name in candidates)
                {
                    try
                    {
                        cls = Java.Lang.Class.ForName(name);
                        _resolvedClass = name;
                        break;
                    }
                    catch { }
                }
                if (cls == null)
                {
                    _launchError = "SettingsActivity не найден, пробовал: " + string.Join(", ", candidates);
                    _tcs.TrySetResult(null);
                    Finish();
                    return;
                }

                // 2) Имя extra-ключа — статическое поле DEVICE_SETTINGS этого класса.
                try
                {
                    // СБОРКА 9: поле может быть объявлено не публичным — пробуем
                    // и GetField (публичные, включая унаследованные), и GetDeclaredField.
                    Java.Lang.Reflect.Field field = null;
                    try { field = cls.GetField("DEVICE_SETTINGS"); } catch { }
                    if (field == null)
                    {
                        try { field = cls.GetDeclaredField("DEVICE_SETTINGS"); } catch { }
                    }
                    Java.Lang.Object keyValue = field != null ? field.Get(null) : null;
                    if (keyValue != null && !string.IsNullOrEmpty(keyValue.ToString()))
                    {
                        _settingsKey = keyValue.ToString();
                    }
                }
                catch { }

                // 3) Открываем родное окно. СБОРКА 10: prefill возвращён — теперь
                // передаётся ТОЛЬКО JSON, который СГЕНЕРИРОВАЛ САМ АТОЛ (сохранённый
                // рабочий): его парсер точно переживёт свой же JSON, и окно
                // откроется уже с выбранным USB. Рукописный {"type":"usb"} не
                // передаётся никогда — им же и была вызвана ошибка 4.
                var intent = new Intent();
                intent.SetClass(this, cls);
                string prefill = Intent != null ? Intent.GetStringExtra(ExtraCurrentSettings) : null;
                if (!string.IsNullOrEmpty(prefill))
                {
                    intent.PutExtra(_settingsKey, prefill);
                }
                StartActivityForResult(intent, RequestCode);
            }
            catch (System.Exception ex)
            {
                _launchError = ex.GetType().Name + ": " + ex.Message;
                _tcs.TrySetResult(null);
                Finish();
            }
        }

        protected override void OnActivityResult(int requestCode, Android.App.Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode == RequestCode)
            {
                string json = null;
                if (resultCode == Android.App.Result.Ok && data != null)
                {
                    // СБОРКА 9: ключ результата пробуем несколькими вариантами —
                    // отражение могло не прочитать поле DEVICE_SETTINGS.
                    json = data.GetStringExtra(_settingsKey);
                    if (string.IsNullOrEmpty(json))
                    {
                        json = data.GetStringExtra(FallbackSettingsKey);
                    }
                    if (string.IsNullOrEmpty(json))
                    {
                        json = data.GetStringExtra("DEVICE_SETTINGS");
                    }
                }
                var tcs = _tcs;
                _tcs = null;
                if (tcs != null)
                {
                    tcs.TrySetResult(json);
                }
            }
            Finish();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            var tcs = _tcs;
            _tcs = null;
            if (tcs != null)
            {
                tcs.TrySetResult(null);
            }
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

        // СТРАТЕГИЯ ПОДКЛЮЧЕНИЯ (СБОРКА 5): ДТО-10 на Android получает JSON-настройки.
        // DefaultUsbSettings — автопоиск USB (как в примерах АТОЛ). Если автопоиск
        // не срабатывает, EnsureOpen перебирает варианты с ЯВНЫМ именем USB-устройства
        // (путь вида /dev/bus/usb/BBB/DDD), а первая удачная стратегия запоминается
        // и дальше используется всегда.
        // ГЛАВНАЯ НАХОДКА (СБОРКА 9/10): {"type":"usb"} на этой версии драйвера
        // НЕ открывает порт (ошибка 4). Рабочий JSON генерирует РОДНОЕ окно
        // настроек АТОЛ — полный объект вида {"Port":"1","UsbDevicePath":"auto",…}.
        // СБОРКА 10: такой JSON сохраняется в файл и грузится при старте.
        private const string DefaultUsbSettings = "{\"type\":\"usb\"}";
        private static string _currentSettingsJson = DefaultUsbSettings;
        private static string _workingSettingsJson = "";   // кэш удачной стратегии
        // --------------------------------------------------------------------
        // СБОРКА 10: файл с рабочим JSON настроек (сгенерирован родным окном
        // АТОЛ). Сохраняется при любом удачном открытии связи, загружается при
        // первом создании драйвера — связь открывается сразу после перезапуска.
        // --------------------------------------------------------------------
        private static readonly string SavedSettingsPath =
            System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "cashplace_atol_settings.json");

        // СБОРКА 11: ошибка записи файла настроек больше не глотается молча —
        // она показывается в отчёте родных настроек и в отчёте теста связи.
        private static string LastSettingsSaveError = "";

        private static void SaveSettingsToFile(string json)
        {
            LastSettingsSaveError = "";
            try
            {
                if (!string.IsNullOrEmpty(json))
                {
                    System.IO.File.WriteAllText(SavedSettingsPath, json);
                }
            }
            catch (System.Exception ex)
            {
                LastSettingsSaveError = ex.GetType().Name + ": " + ex.Message;
                AtolPrinterConfig.Log("AtolSettings",
                    "Не удалось записать файл настроек: " + LastSettingsSaveError);
            }
        }

        private static string LoadSavedSettings()
        {
            try
            {
                if (System.IO.File.Exists(SavedSettingsPath))
                {
                    string json = System.IO.File.ReadAllText(SavedSettingsPath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        return json;
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>Из каких настроек сейчас работает драйвер (для отчётов).</summary>
        private static string DescribeSettingsSource()
        {
            lock (_fptrLock)
            {
                if (!string.IsNullOrEmpty(_workingSettingsJson))
                {
                    return "рабочие настройки этой сессии (JSON родного окна АТОЛ)";
                }
                if (LoadSavedSettings() != "")
                {
                    return "сохранённый JSON родного окна АТОЛ (файл)";
                }
                return "автопоиск USB по умолчанию (рукописный JSON)";
            }
        }

        // Отчёт о переборе стратегий подключения — печатается на экране теста связи.
        public static string LastOpenAttemptsReport = "";

        // ЗАЩИТА 1 (паритет с десктопом): от двойного клика — документы "в печати"
        private static readonly System.Collections.Generic.HashSet<string> _currentlyPrintingDocs =
            new System.Collections.Generic.HashSet<string>();

        private const string ActionUsbPermission = "com.yourname.cash8mobile.USB_PERMISSION";

        // VID устройств АТОЛ: 0x2912 (10514) — основной VID ФР АТОЛ (тот же, что
        // прописан в device_filter.xml), 0x2E83 — дополнительный VID из примеров
        // АТОЛ, 0x0403 — мосты FTDI (часть моделей АТОЛ 30Ф/11Ф).
        private static readonly int[] KnownFrVendorIds = new int[] { 0x2912, 0x2E83, 0x0403 };

        private static bool IsKnownFrVendor(int vendorId)
        {
            foreach (int vid in KnownFrVendorIds)
            {
                if (vendorId == vid)
                {
                    return true;
                }
            }
            return false;
        }

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
                    // РАБОЧИЙ вариант (проверено на планшете): контекст Activity.
                    // С Application.Context (СБОРКА 4) связь не открывалась — ошибка 4.
                    Context context = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
                                      ?? Android.App.Application.Context;

                    // Если в вашем биндинге у FptrImpl конструктор без параметров — уберите аргумент.
                    _fptr = new FptrImpl(context);

                    // ДТО-10 на Android: настройки передаются JSON-СТРОКОЙ
                    // (в этом aar setSettings(String), не JSONObject).
                    // USB-подключение к ФР (стратегия — кэш удачного варианта).
                    // СБОРКА 10: при самом первом создании драйвера грузим
                    // сохранённый рабочий JSON (от родного окна АТОЛ), если он есть.
                    if (_currentSettingsJson == DefaultUsbSettings)
                    {
                        string saved = LoadSavedSettings();
                        if (saved != "")
                        {
                            _currentSettingsJson = saved;
                        }
                    }
                    _fptr.SetSettings(_currentSettingsJson);

                    AtolPrinterConfig.Log("Connect",
                        "Драйвер АТОЛ создан (контекст: " +
                        (context == Android.App.Application.Context ? "Application.Context" : "Activity") +
                        "), настройки: " + _currentSettingsJson);
                }
                return _fptr;
            }
        }

        // ------------------------------------------------------------------------
        // АНТИ-ОШИБКА-4: полный сброс драйвера.
        // Старый экземпляр IFptr после переподключения ФР по USB или перезагрузки
        // планшета может держать мёртвый USB-коннект: тогда Open() возвращает
        // ошибку 4 "порт недоступен", хотя разрешение на USB выдано.
        // Сброс закрывает экземпляр; следующий GetFptr() создаст свежий.
        // ------------------------------------------------------------------------
        private static void DestroyFptrInstance()
        {
            try
            {
                if (_fptr != null)
                {
                    // СБОРКА 7: Close() вызывается ВСЕГДА, независимо от IsOpened.
                    // После неудачного Open() флаг IsOpened=false, но внутри драйвера
                    // может остаться живой ("полуоткрытый") USB-коннект — одна из причин
                    // УПОРНОЙ ошибки 4. Между попытками экземпляр пересоздаётся, и
                    // каждый пропуск Close() здесь — протечка порта до перезапуска.
                    try { _fptr.Close(); } catch { }
                    // Если в вашем биндинге есть метод Destroy() — раскомментируйте строку ниже:
                    // try { _fptr.Destroy(); } catch { }
                }
            }
            catch { }
            _fptr = null;
        }

        /// <summary>
        /// Полный сброс связи с ФР: закрывает текущий экземпляр драйвера, следующий
        /// вызов любой операции откроет USB заново на свежем экземпляре.
        /// Вызывайте, если подозреваете "зависший" USB-стек (упорная ошибка 4).
        /// </summary>
        public static void ResetConnection()
        {
            lock (_fptrLock)
            {
                DestroyFptrInstance();
            }
            AtolPrinterConfig.Log("Connect", "ResetConnection: драйвер ФР сброшен, следующий вызов откроет связь заново");
        }

        // ------------------------------------------------------------------------
        // СБОРКА 8: связь настройками, которые СГЕНЕРИРУЕТ САМ АТОЛ.
        // Открываем родное окно настроек драйвера (SettingsActivity из
        // libfptr10.aar) через AtolSettingsBridgeActivity, получаем JSON,
        // применяем его на СВЕЖЕМ экземпляре и пробуем Open(). Если родные
        // настройки АТОЛ тоже не открывают порт — проблема не в настройках
        // кассы (готовый аргумент для поддержки АТОЛ).
        // ------------------------------------------------------------------------
        public static async System.Threading.Tasks.Task<string> RunNativeAtolSettingsFlowAsync()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[Модуль ФР: СБОРКА 11 — настройки АТОЛ реально сохраняются в файл]");

            // СБОРКА 9: если в прошлый раз приложение «свернулось», перед смертью
            // процесса исключение было записано в журнал. Показываем его первым.
            string crashReport = AtolSettingsBridgeActivity.ReadAndClearCrashReport();
            if (crashReport != "")
            {
                sb.AppendLine("=== ПОСЛЕДНЕЕ ПАДЕНИЕ ПРИЛОЖЕНИЯ (журнал) ===");
                sb.AppendLine(crashReport);
                sb.AppendLine("=== КОНЕЦ ЖУРНАЛА ПАДЕНИЙ ===");
                sb.AppendLine();
            }

            Context context = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
                              ?? Android.App.Application.Context;
            if (context == null)
            {
                sb.AppendLine("Не удалось получить Activity для запуска окна настроек.");
                AtolPrinterConfig.Log("AtolSettings", sb.ToString());
                return sb.ToString();
            }

            // СБОРКА 10: prefill = сохранённый РАБОЧИЙ JSON (его сгенерировал сам
            // АТОЛ — окно переживёт свой же JSON и откроется уже с USB).
            // Рукописный {"type":"usb"} не передаём: окно тогда стартует
            // с Bluetooth по умолчанию и выглядит «несохранённым».
            string prefill = LoadSavedSettings();
            if (prefill == "")
            {
                lock (_fptrLock)
                {
                    if (!string.IsNullOrEmpty(_workingSettingsJson))
                    {
                        prefill = _workingSettingsJson;
                    }
                }
            }

            sb.AppendLine("Открываю родное окно настроек драйвера АТОЛ…");
            sb.AppendLine("В окне: Порт -> USB, при необходимости выберите «Устройство».");
            sb.AppendLine("СОХРАНЕНИЕ: стрелка <- в шапке окна (сверху слева) — окно само закроется.");
            sb.AppendLine("Системная «назад» (жест/кнопка внизу экрана) = отмена.");

            var launchTask = AtolSettingsBridgeActivity.LaunchAsync(context, prefill);
            System.Threading.Tasks.Task finished = await System.Threading.Tasks.Task.WhenAny(
                (System.Threading.Tasks.Task)launchTask,
                System.Threading.Tasks.Task.Delay(System.TimeSpan.FromMinutes(20)));
            bool timedOut = !System.Object.ReferenceEquals(finished, (System.Threading.Tasks.Task)launchTask);
            string returnedJson = timedOut ? null : await launchTask;

            sb.AppendLine("Запуск окна: " + AtolSettingsBridgeActivity.LastLaunchReport);
            sb.AppendLine();

            if (string.IsNullOrEmpty(returnedJson))
            {
                sb.AppendLine(timedOut
                    ? "НАСТРОЙКИ НЕ ПОЛУЧЕНЫ: окно не вернуло результат за 20 минут."
                    : "НАСТРОЙКИ НЕ ПОЛУЧЕНЫ: окно закрыто или отменено.");
                AtolPrinterConfig.Log("AtolSettings", sb.ToString());
                return sb.ToString();
            }

            sb.AppendLine("АТОЛ вернул настройки:");
            sb.AppendLine(returnedJson);
            sb.AppendLine();

            // СБОРКА 11 (ИСПРАВЛЕНИЕ СБОРКИ 10): сохраняем JSON СРАЗУ, до попытки
            // открыть связь. В СБОРКЕ 10 сохранение стояло только внутри EnsureOpen,
            // но после успешного открытия связь была уже открыта: EnsureOpen видел
            // IsOpened == true и выходил, не доходя до записи файла. Файл не
            // создавался НИКОГДА — выбор «USB» слетал при каждом перезапуске кассы.
            SaveSettingsToFile(returnedJson);
            if (LastSettingsSaveError == "")
            {
                sb.AppendLine("СБОРКА 11: этот JSON записан в файл " +
                              System.IO.Path.GetFileName(SavedSettingsPath) + " —");
                sb.AppendLine("после перезапуска касса откроет связь именно им.");
            }
            else
            {
                sb.AppendLine("ВНИМАНИЕ (СБОРКА 11): записать файл настроек НЕ удалось: " +
                              LastSettingsSaveError);
            }
            sb.AppendLine();

            sb.AppendLine("Пробую открыть связь полученными настройками (свежий экземпляр драйвера)…");

            bool opened = false;
            int errCode = -1;
            string errDesc = "";
            lock (_fptrLock)
            {
                DestroyFptrInstance();
                _currentSettingsJson = returnedJson;
                IFptr fptr = GetFptr();
                if (fptr.Open() == 0)
                {
                    opened = true;
                    _workingSettingsJson = returnedJson;
                }
                else
                {
                    errCode = fptr.ErrorCode();
                    errDesc = fptr.ErrorDescription();
                    // Вернуть настройки к автопоиску, оставить СВЕЖИЙ экземпляр
                    DestroyFptrInstance();
                    _currentSettingsJson = DefaultUsbSettings;
                    GetFptr();
                }
            }

            if (opened)
            {
                sb.AppendLine("СВЯЗЬ ОТКРЫТА РОДНЫМИ НАСТРОЙКАМИ АТОЛ!");
                sb.AppendLine("СБОРКА 11: настройки сохранены в файл — теперь связь будет открываться");
                sb.AppendLine("сразу после запуска кассы, без повторной настройки.");
                sb.AppendLine("Нажмите «ОБНОВИТЬ», чтобы увидеть полный статус ФР (и попробуйте X-ОТЧЁТ).");
            }
            else
            {
                sb.AppendLine("Родными настройками АТОЛ связь НЕ открылась: ошибка " + errCode + " " + errDesc);
                sb.AppendLine("Важный результат: если даже родное окно АТОЛ не открывает порт, проблема");
                sb.AppendLine("не в настройках кассы. Пришлите весь этот текст целиком.");
                // СБОРКА 11: JSON уже записан в файл (выше), поэтому выбор «USB»
                // не пропадёт: после перезапуска касса сначала попробует его.
                sb.AppendLine("СБОРКА 11: выбранный «USB» всё равно сохранён в файл — после");
                sb.AppendLine("перезапуска касса сначала попробует открыть связь именно им.");
            }

            AtolPrinterConfig.Log("AtolSettings", sb.ToString());
            return sb.ToString();
        }

        // ------------------------------------------------------------------------
        // Гарантированно открытая связь (СБОРКА 7).
        // Данные СБОРКИ 6: порт свободен (зонд ДО и ПОСЛЕ — OpenDevice и захват
        // интерфейса ОК), разрешение есть, но драйвер даёт ошибку 4 ПО ВСЕМ
        // стратегиям. Значит, "залипает" СОСТОЯНИЕ ЭКЗЕМПЛЯРА ДРАЙВЕРА: после
        // первой неудачи Open() этот экземпляр не открывается уже ни с какими
        // настройками. Поэтому каждая повторная попытка выполняется на СВЕЖЕМ
        // экземпляре (пересоздание лечило ошибку 4 вчера).
        // Последовательность попыток:
        //   1) текущий экземпляр, текущие настройки (сохранённый JSON АТОЛ или автопоиск);
        //   2) пересоздание + автопоиск USB;
        //   3) пересоздание + явный путь {"type":"usb","usbDeviceName":"..."};
        //   4) пересоздание + явный путь {"type":"usb","deviceName":"..."}.
        // ВАЖНО: EnsureOpen возвращает ЖИВОЙ экземпляр через out — вызывающий код
        // работает только с ним (ссылки на пересозданные экземпляры невалидны).
        // Отчёт о переборе — в LastOpenAttemptsReport (виден на экране теста).
        // ------------------------------------------------------------------------
        private static bool EnsureOpen(out IFptr fptr, out int errorCode, out string errorDescription)
        {
            lock (_fptrLock)
            {
                errorCode = 0;
                errorDescription = "";
                fptr = GetFptr();
                if (fptr.IsOpened)
                {
                    return true;
                }

                EnsureUsbPermissions();
                var attempts = new System.Text.StringBuilder();

                // Явное открытие связи с ФР: SetSettings только задаёт параметры,
                // связь устанавливает Open() — как в примерах АТОЛ для Android.
                if (fptr.Open() == 0)
                {
                    _workingSettingsJson = _currentSettingsJson;
                    SaveSettingsToFile(_currentSettingsJson);   // СБОРКА 10
                    LastOpenAttemptsReport = "";
                    return true;
                }

                // --- АНТИ-ОШИБКА-4: первая попытка не прошла -------------------------
                errorCode = fptr.ErrorCode();
                errorDescription = fptr.ErrorDescription();
                attempts.Append("1) автопоиск USB: ошибка ").Append(errorCode)
                        .Append(" ").AppendLine(errorDescription);
                AtolPrinterConfig.Log("Connect",
                    "Open() не удался (ошибка " + errorCode + " " + errorDescription +
                    "): пересоздаю драйвер и перебираю стратегии");

                // Путь ФР в системе (/dev/bus/usb/BBB/DDD) — для стратегий с явным именем.
                string usbPath = "";
                UsbDevice frDevice = FindFrUsbDevice();
                if (frDevice != null)
                {
                    usbPath = frDevice.DeviceName ?? "";
                }

                var candidates = new System.Collections.Generic.List<(string Label, string Json)>();
                candidates.Add(("пересоздание + автопоиск USB", DefaultUsbSettings));
                if (usbPath != "")
                {
                    candidates.Add(("пересоздание + явный путь usbDeviceName=" + usbPath,
                        "{\"type\":\"usb\",\"usbDeviceName\":\"" + usbPath + "\"}"));
                    candidates.Add(("пересоздание + явный путь deviceName=" + usbPath,
                        "{\"type\":\"usb\",\"deviceName\":\"" + usbPath + "\"}"));
                }

                int attemptNo = 1;
                foreach (var candidate in candidates)
                {
                    attemptNo++;
                    System.Threading.Thread.Sleep(400);   // дать USB-стеку момент на освобождение порта

                    // СБОРКА 7: каждая попытка — на СВЕЖЕМ экземпляре драйвера.
                    // DestroyFptrInstance закрывает и "полуоткрытый" USB-коннект
                    // неудачной попытки; новый экземпляр получает настройки кандидата.
                    DestroyFptrInstance();
                    _currentSettingsJson = candidate.Json;
                    fptr = GetFptr();

                    if (fptr.Open() == 0)
                    {
                        _workingSettingsJson = candidate.Json;   // запомнить удачную стратегию
                        SaveSettingsToFile(candidate.Json);      // СБОРКА 10: сохранить рабочий JSON
                        LastOpenAttemptsReport = "";
                        AtolPrinterConfig.Log("Connect",
                            "Связь с ФР установлена (стратегия: " + candidate.Label + ")");
                        errorCode = 0;
                        errorDescription = "";
                        return true;
                    }
                    errorCode = fptr.ErrorCode();
                    errorDescription = fptr.ErrorDescription();
                    attempts.Append(attemptNo).Append(") ").Append(candidate.Label)
                            .Append(": ошибка ").Append(errorCode).Append(" ").AppendLine(errorDescription);
                }

                // Все попытки не удались — вернуть настройки к автопоиску и оставить
                // СВЕЖИЙ экземпляр (без "полуоткрытых" коннектов) для следующих вызовов.
                DestroyFptrInstance();
                _currentSettingsJson = DefaultUsbSettings;
                fptr = GetFptr();

                LastOpenAttemptsReport = attempts.ToString();
                AtolPrinterConfig.Log("Connect",
                    "Не удалось открыть связь с ФР ни одной стратегией:\r\n" + attempts.ToString());
                return false;
            }
        }

        // Android требует разрешения на работу с USB-устройством. Драйвер обычно
        // запрашивает его сам, но на всякий случай просим заранее (идемпотентно).
        // Отчёт о последнем запросе разрешения — печатается в блоке ДИАГНОСТИКА USB,
        // чтобы сбои запроса были ВИДНЫ на экране, а не только в логе.
        public static string LastUsbActionReport = "";

        private static void EnsureUsbPermissions()
        {
            var report = new System.Text.StringBuilder();
            try
            {
                // Официальный пример АТОЛ использует getApplicationContext().
                Context context = Android.App.Application.Context;
                UsbManager usbManager = context.GetSystemService(Context.UsbService) as UsbManager;
                if (usbManager == null || usbManager.DeviceList == null)
                {
                    LastUsbActionReport = "Отчёт USB: UsbManager недоступен.";
                    return;
                }

                bool requestedAny = false;
                foreach (UsbDevice device in usbManager.DeviceList.Values)
                {
                    if (usbManager.HasPermission(device))
                    {
                        continue;
                    }
                    if (!IsKnownFrVendor(device.VendorId))
                    {
                        continue;
                    }

                    Intent intent = new Intent(ActionUsbPermission);
                    // ВАЖНО (Android 14+): для изменяемого (Mutable) PendingIntent
                    // с неявным интентом ОБЯЗАТЕЛЬНО указывать пакет, иначе
                    // PendingIntent.GetBroadcast бросает исключение и системный
                    // диалог «Разрешить» вообще не появляется.
                    intent.SetPackage(context.PackageName);

                    PendingIntent pendingIntent = PendingIntent.GetBroadcast(
                        context, 0, intent, PendingIntentFlags.Mutable);

                    AtolPrinterConfig.Log("USB",
                        "Запрос разрешения для USB устройства VID=0x" + device.VendorId.ToString("X4"));

                    usbManager.RequestPermission(device, pendingIntent);
                    // Системный диалог появится на экране планшета — кассир должен нажать "Разрешить".
                    requestedAny = true;
                    report.Append("запрос разрешения отправлен для VID=0x")
                          .Append(device.VendorId.ToString("X4"))
                          .Append(" — на экране должен появиться системный диалог, нажмите «Разрешить».");
                }

                if (!requestedAny)
                {
                    report.Append("новых запросов не отправлено: либо разрешение уже выдано, либо устройство с VID ФР не найдено.");
                }
            }
            catch (System.Exception ex)
            {
                // Раньше эта ошибка проглатывалась молча — из-за неё диалог мог не появляться.
                report.Append("ОШИБКА запроса разрешения: ").Append(ex.GetType().Name)
                      .Append(": ").Append(ex.Message);
                AtolPrinterConfig.Log("USB", "Не удалось запросить USB-разрешение: " + ex);
            }
            LastUsbActionReport = report.ToString();
        }

        // ------------------------------------------------------------------------
        // ДИАГНОСТИКА USB: полный список USB-устройств планшета с VID/PID и
        // отметкой, есть ли у НАШЕГО приложения разрешение на каждое из них.
        // Позволяет отличить три ситуации:
        //   а) ФР не виден вообще (выключен / кабель / порт) — списка нет или VID чужой;
        //   б) ФР виден, разрешения у нашего приложения нет — просим и ждём «Разрешить»;
        //   в) ФР виден, разрешение есть, но порт занят другим приложением (ошибка 4).
        // Возвращает true, если найдено устройство с VID из списка известных ФР.
        // ------------------------------------------------------------------------
        private static bool AppendUsbDiagnostics(System.Text.StringBuilder sb)
        {
            try
            {
                // Официальный пример АТОЛ использует getApplicationContext().
                Context context = Android.App.Application.Context;
                UsbManager usbManager = context.GetSystemService(Context.UsbService) as UsbManager;
                if (usbManager == null || usbManager.DeviceList == null || usbManager.DeviceList.Count == 0)
                {
                    sb.AppendLine("ДИАГНОСТИКА USB: планшет НЕ ВИДИТ ни одного USB-устройства.");
                    sb.AppendLine("=> Проверьте: ФР включён (экран/индикатор горит)? Кабель тот же, что при удачной связи?");
                    return false;
                }

                sb.AppendLine("ДИАГНОСТИКА USB, устройства планшета:");
                bool frSeen = false;
                foreach (UsbDevice device in usbManager.DeviceList.Values)
                {
                    bool known = IsKnownFrVendor(device.VendorId);
                    bool perm = usbManager.HasPermission(device);
                    sb.AppendLine("  VID=0x" + device.VendorId.ToString("X4") + " (" + device.VendorId +
                                  ")  PID=0x" + device.ProductId.ToString("X4") + " (" + device.ProductId + ")" +
                                  (known ? "  <- похоже на ФР" : "") +
                                  "  Разрешение: " + (perm ? "ЕСТЬ" : "НЕТ"));
                    string devName = ((device.ManufacturerName ?? "") + " / " + (device.ProductName ?? "")).Trim(' ', '/');
                    if (devName != "")
                    {
                        sb.AppendLine("      устройство: " + devName);
                    }
                    if (!string.IsNullOrEmpty(device.DeviceName))
                    {
                        // Путь вида /dev/bus/usb/BBB/DDD — именно его ДТО-10 использует
                        // для ЯВНОГО подключения; МЕНЯЕТСЯ после переподключения кабеля!
                        sb.AppendLine("      путь: " + device.DeviceName);
                    }
                    if (known)
                    {
                        frSeen = true;
                    }
                }
                if (!string.IsNullOrEmpty(LastUsbActionReport))
                {
                    sb.AppendLine("Отчёт о запросе разрешения (прошлое нажатие кнопки): " + LastUsbActionReport);
                }
                return frSeen;
            }
            catch (System.Exception ex)
            {
                sb.AppendLine("ДИАГНОСТИКА USB: не удалось получить список устройств: " + ex.Message);
                return false;
            }
        }

        // -----------------------------------------------------------------------
        // Поиск ФР среди USB-устройств планшета (первое устройство с известным VID).
        // -----------------------------------------------------------------------
        private static UsbDevice FindFrUsbDevice()
        {
            try
            {
                Context context = Android.App.Application.Context;
                UsbManager usbManager = context.GetSystemService(Context.UsbService) as UsbManager;
                if (usbManager == null || usbManager.DeviceList == null)
                {
                    return null;
                }
                foreach (UsbDevice device in usbManager.DeviceList.Values)
                {
                    if (IsKnownFrVendor(device.VendorId))
                    {
                        return device;
                    }
                }
            }
            catch { }
            return null;
        }

        // -----------------------------------------------------------------------
        // ПРЯМАЯ ПРОВЕРКА ПОРТА (мимо драйвера АТОЛ): открываем USB-устройство
        // средствами Android и захватываем его интерфейс. Результат сразу
        // показывает, ГДЕ проблема: в порту (занят другим процессом/не отвечает)
        // или в драйвере (тогда помогают стратегии с явным именем устройства).
        // -----------------------------------------------------------------------
        private static string BuildDirectPortProbeReport(string phase)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("ПРЯМАЯ ПРОВЕРКА ПОРТА (" + phase + ", без драйвера АТОЛ):");
            try
            {
                Context context = Android.App.Application.Context;
                UsbManager usbManager = context.GetSystemService(Context.UsbService) as UsbManager;
                UsbDevice fr = FindFrUsbDevice();
                if (usbManager == null || fr == null)
                {
                    sb.AppendLine("  ФР не найден в списке USB-устройств.");
                    return sb.ToString();
                }

                var conn = usbManager.OpenDevice(fr);
                if (conn == null)
                {
                    sb.AppendLine("  OpenDevice() = null: разрешение не действует (выдайте заново кнопкой).");
                    return sb.ToString();
                }
                sb.AppendLine("  OpenDevice(): ОК — разрешение действует.");

                bool claimed = false;
                string claimErr = "";
                try
                {
                    if (fr.InterfaceCount > 0)
                    {
                        var iface = fr.GetInterface(0);
                        claimed = conn.ClaimInterface(iface, true);
                    }
                    else
                    {
                        claimErr = "у устройства нет интерфейсов";
                    }
                }
                catch (System.Exception exClaim)
                {
                    claimErr = exClaim.Message;
                }

                if (claimed)
                {
                    sb.AppendLine("  Захват интерфейса: ОК — порт свободен и доступен НАШЕМУ приложению.");
                    sb.AppendLine("  ВЫВОД: порт работает; причина ошибки 4 — настройки/состояние драйвера,");
                    sb.AppendLine("  а НЕ другой приложение. Стратегии с явным путём должны помочь.");
                }
                else
                {
                    sb.AppendLine("  Захват интерфейса: ОТКАЗ — интерфейс уже кем-то захвачен. " + claimErr);
                    sb.AppendLine("  ВЫВОД: для зонда ДО — порт держит ДРУГОЕ приложение; для зонда ПОСЛЕ —");
                    sb.AppendLine("  возможно, НАШ драйвер после неудачных Open(). Сравните оба зонда.");
                }

                try
                {
                    if (claimed && fr.InterfaceCount > 0)
                    {
                        conn.ReleaseInterface(fr.GetInterface(0));
                    }
                }
                catch { }
                try { conn.Close(); } catch { }
            }
            catch (System.Exception exProbe)
            {
                sb.AppendLine("  Ошибка прямой проверки: " + exProbe.Message);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------------
        // Диалоги (замена MessageBoxHelper) — всегда на главном потоке.
        // ------------------------------------------------------------------------
        private static System.Threading.Tasks.Task ShowAlertAsync(string title, string message)
        {
            AtolPrinterConfig.Log("Alert", title + ": " + message);
            return Microsoft.Maui.ApplicationModel.MainThread.InvokeOnMainThreadAsync(async () =>
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
            return Microsoft.Maui.ApplicationModel.MainThread.InvokeOnMainThreadAsync(async () =>
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

            if (!EnsureOpen(out IFptr fptr, out int openCode, out string openDesc))
            {
                await ShowAlertAsync("Ошибка связи с ФР",
                    "Не удалось подключиться к фискальному регистратору.\nОшибка " + openCode + ": " + openDesc +
                    "\n\nЕсли ошибка 4: закройте сервисную утилиту АТОЛ (Настройки -> Приложения -> Остановить)," +
                    " она удерживает порт ФР.");
                return FiscalPrintResult.Fail(openCode, openDesc);
            }

            // --- Оператор (теги 1021/1203), паритет с десктопом ---
            fptr.SetParam(1021, AtolPrinterConfig.OperatorName);
            fptr.SetParam(1203, AtolPrinterConfig.OperatorInn);
            fptr.OperatorLogin();

            // --- Тип чека ---
            if (request.ReceiptType == 0)
            {
                fptr.SetParam(IFptr.LibfptrParamReceiptType, IFptr.LibfptrRtSell);
            }
            else if (request.ReceiptType == 1)
            {
                fptr.SetParam(IFptr.LibfptrParamReceiptType, IFptr.LibfptrRtSellReturn);
            }
            else
            {
                return FiscalPrintResult.Fail(-1, "Тип чека " + request.ReceiptType + " на планшете не поддерживается (0=продажа, 1=возврат)");
            }

            // --- Телефон/email покупателя (тег 1008) ---
            if (!string.IsNullOrWhiteSpace(request.ClientContact))
            {
                fptr.SetParam(1008, request.ClientContact.Trim());
                fptr.SetParam(IFptr.LibfptrParamReceiptElectronically, true);
            }

            // --- Система налогообложения (тег 1055), паритет с десктопом ---
            if (AtolPrinterConfig.SystemTaxation == 1)
            {
                fptr.SetParam(1055, IFptr.LibfptrTtOsn);
            }
            else if (AtolPrinterConfig.SystemTaxation == 2)
            {
                fptr.SetParam(1055, IFptr.LibfptrTtUsnIncomeOutcome);
            }
            else
            {
                fptr.SetParam(1055, IFptr.LibfptrTtUsnIncome);
            }

            // --- Открытие чека ---
            if (fptr.OpenReceipt() != 0)
            {
                int errorCode = fptr.ErrorCode();
                string errorDesc = fptr.ErrorDescription();
                AtolPrinterConfig.Log("PrintReceipt",
                    "Ошибка открытия чека " + docKey + ": " + errorCode + " " + errorDesc);

                await ShowAlertAsync("Ошибка открытия чека",
                    "Ошибка при открытии чека.\nОшибка " + errorCode + ": " + errorDesc);

                if (errorCode == 82)
                {
                    fptr.CancelReceipt();
                    await ShowAlertAsync("Ошибка при печати чека", "Попробуйте распечатать чек еще раз");
                }
                else if (errorCode == 44)
                {
                    try { fptr.CancelReceipt(); } catch { }
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
                        fptr.SetParam(1212, 32);
                    }
                    else
                    {
                        // Маркированный товар
                        // TODO: ЧЗ-валидация (cdn_check) на планшете пока не портирована,
                        // поэтому 1260 не формируется, результат онлайн-проверки = 0.
                        fptr.SetParam(IFptr.LibfptrParamMarkingCode, mark);
                        fptr.SetParam(IFptr.LibfptrParamMarkingCodeType, IFptr.LibfptrMct12Auto);
                        if (request.ReceiptType == 0)
                        {
                            fptr.SetParam(IFptr.LibfptrParamMarkingCodeStatus, IFptr.LibfptrMesPieceSold);
                        }
                        else
                        {
                            fptr.SetParam(IFptr.LibfptrParamMarkingCodeStatus, IFptr.LibfptrMesPieceReturn);
                        }
                        fptr.SetParam(IFptr.LibfptrParamCommodityName, code + " " + tovar);
                        fptr.SetParam(IFptr.LibfptrParamMarkingCodeOnlineValidationResult, 0);
                        fptr.SetParam(IFptr.LibfptrParamMarkingProcessingMode, 0);
                    }
                }
                else
                {
                    // Акцизный товар
                    fptr.SetParam(1212, 2);
                }

                fptr.SetParam(IFptr.LibfptrParamCommodityName, code + " " + tovar);
                fptr.SetParam(IFptr.LibfptrParamPrice, p.PriceAtDiscount.ToString().Replace(",", "."));
                fptr.SetParam(IFptr.LibfptrParamMeasurementUnit,
                    p.IsWeighted ? IFptr.LibfptrIuKilogram : IFptr.LibfptrIuPiece);
                fptr.SetParam(IFptr.LibfptrParamQuantity, p.Quantity.ToString().Replace(",", "."));

                int? taxType = MapVat(p, request.ReceiptType);
                if (taxType == int.MinValue)
                {
                    await ShowAlertAsync("Проверка ставки НДС", "Неизвестная ставка НДС у товара " + code + " " + tovar);
                    fptr.CancelReceipt();
                    return FiscalPrintResult.Fail(-1, "Неизвестная ставка НДС: " + p.StavkaNds);
                }
                if (taxType.HasValue)
                {
                    fptr.SetParam(IFptr.LibfptrParamTaxType, taxType.Value);
                }

                if (request.ReceiptType == 0)
                {
                    // Предмет расчёта: сертификат = 3, товар = 4
                    fptr.SetParam(1214, p.IsSertificate ? 3 : 4);
                }

                fptr.Registration();
                if (fptr.ErrorCode() > 0)
                {
                    string msg = "При печати позиции " + code + " " + tovar + " произошли ошибки\r\n" +
                                 "Код ошибки " + fptr.ErrorCode() + "\r\n" + fptr.ErrorDescription();
                    AtolPrinterConfig.Log("PrintReceipt", msg);
                    await ShowAlertAsync("Ошибка при печати", msg);
                    fptr.CancelReceipt();
                    return FiscalPrintResult.Fail(fptr.ErrorCode(), fptr.ErrorDescription());
                }
            }

            // --- Итог чека (сумма позиций, паритет calculation_of_the_sum_of_the_document) ---
            double total = System.Math.Round(
                SumPositions(request.Positions), 2);
            fptr.SetParam(IFptr.LibfptrParamSum, total);
            fptr.ReceiptTotal();

            // --- Оплаты, паритет с десктопом ---
            // QR/СБП входит в безналичную сумму => LIBFPTR_PT_ELECTRONICALLY
            if (request.SumCash > 0)
            {
                fptr.SetParam(IFptr.LibfptrParamPaymentType, IFptr.LibfptrPtCash);
                fptr.SetParam(IFptr.LibfptrParamPaymentSum, request.SumCash);
                fptr.Payment();
            }
            if (request.SumElectron > 0)
            {
                fptr.SetParam(IFptr.LibfptrParamPaymentType, IFptr.LibfptrPtElectronically);
                fptr.SetParam(IFptr.LibfptrParamPaymentSum, request.SumElectron);
                fptr.Payment();
            }
            if (request.SumPrepaid > 0)
            {
                fptr.SetParam(IFptr.LibfptrParamPaymentType, IFptr.LibfptrPtPrepaid);
                fptr.SetParam(IFptr.LibfptrParamPaymentSum, request.SumPrepaid);
                fptr.Payment();
            }

            // --- Отложенные строки чека ---
            if (request.ReceiptType == 0 && !string.IsNullOrEmpty(request.DiscountNote))
            {
                fptr.SetParam(IFptr.LibfptrParamText, request.DiscountNote);
                fptr.SetParam(IFptr.LibfptrParamDefer, IFptr.LibfptrDeferPost);
                fptr.SetParam(IFptr.LibfptrParamAlignment, IFptr.LibfptrAlignmentCenter);
                fptr.PrintText();
            }

            string shopLine = AtolPrinterConfig.NickShop + "-" + AtolPrinterConfig.CashDeskNumber + "-" + docKey;
            fptr.SetParam(IFptr.LibfptrParamText, shopLine);
            fptr.SetParam(IFptr.LibfptrParamDefer, IFptr.LibfptrDeferPost);
            fptr.SetParam(IFptr.LibfptrParamAlignment, IFptr.LibfptrAlignmentLeft);
            fptr.PrintText();

            // TODO: реклама (print_fiscal_advertisement) — на планшете не портирована.

            // --- Пользовательский реквизит TLV 1084 "NumCheckShop" (паритет) ---
            fptr.SetParam(1085, "NumCheckShop");
            fptr.SetParam(1086, shopLine);
            fptr.UtilFormTlv();
            byte[] userAttribute = fptr.GetParamByteArray(IFptr.LibfptrParamTagValue);
            fptr.SetNonPrintableParam(1084, userAttribute);

            if (fptr.ErrorCode() > 0)
            {
                string msg = "При печати чека произошли ошибки\r\nКод ошибки " + fptr.ErrorCode() +
                             "\r\n" + fptr.ErrorDescription();
                AtolPrinterConfig.Log("PrintReceipt", msg);
                await ShowAlertAsync("Ошибка при печати", msg);
                fptr.CancelReceipt();
                return FiscalPrintResult.Fail(fptr.ErrorCode(), fptr.ErrorDescription());
            }

            fptr.CloseReceipt();

            // --- Ожидание закрытия документа (паритет с десктопом) ---
            while (fptr.CheckDocumentClosed() < 0)
            {
                await ShowAlertAsync("Ошибка при печати чека",
                    fptr.ErrorCode() + " " + fptr.ErrorDescription());
                if (!await ShowConfirmAsync("Ошибка при печати чека", "Продолжать попытки печати чека?"))
                {
                    error = true;
                    break;
                }
            }

            if (!error && !fptr.GetParamBool(IFptr.LibfptrParamDocumentClosed))
            {
                fptr.CancelReceipt();
                string msg = "Не удалось напечатать документ (Ошибка \"" + fptr.ErrorDescription() +
                             "\"). Устраните неполадку и повторите.";
                AtolPrinterConfig.Log("PrintReceipt", msg);
                await ShowAlertAsync("Ошибка при печати чека", msg);
                return FiscalPrintResult.Fail(fptr.ErrorCode(), fptr.ErrorDescription());
            }

            // --- Допечатка при закончившейся бумаге (паритет) ---
            if (!error && !fptr.GetParamBool(IFptr.LibfptrParamDocumentPrinted))
            {
                while (fptr.ContinuePrint() < 0)
                {
                    await ShowAlertAsync("Ошибка при печати чека",
                        "Не удалось напечатать документ (Ошибка \"" + fptr.ErrorDescription() +
                        "\"). Устраните неполадку и повторите.");
                    fptr.SetParam(IFptr.LibfptrParamDataType, IFptr.LibfptrDtShortStatus);
                    fptr.QueryData();
                    if (!fptr.GetParamBool(IFptr.LibfptrParamReceiptPaperPresent))
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
                if (stavka == 0) return IFptr.LibfptrTaxVat0;
                if (stavka == 10) return IFptr.LibfptrTaxVat10;
                if (stavka == 18) return IFptr.LibfptrTaxVat20;
                if (stavka == 20) return year >= 2026 ? IFptr.LibfptrTaxVat22 : IFptr.LibfptrTaxVat20;
                if (stavka == 22)
                {
                    return cert ? (year >= 2026 ? IFptr.LibfptrTaxVat122 : IFptr.LibfptrTaxVat120)
                                : IFptr.LibfptrTaxVat22;
                }
                return int.MinValue;
            }
            else
            {
                int ndsIp = AtolPrinterConfig.NdsIp;
                if (ndsIp == 0) return IFptr.LibfptrTaxNo;
                if (ndsIp == 5) return cert ? IFptr.LibfptrTaxVat105 : IFptr.LibfptrTaxVat5;
                if (ndsIp == 7) return cert ? IFptr.LibfptrTaxVat107 : IFptr.LibfptrTaxVat7;
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
                if (!EnsureOpen(out IFptr fptr, out int code, out string desc))
                {
                    return FiscalPrintResult.Fail(code, desc);
                }
                fptr.SetParam(1021, AtolPrinterConfig.OperatorName);
                fptr.SetParam(1203, AtolPrinterConfig.OperatorInn);
                fptr.OperatorLogin();
                fptr.SetParam(IFptr.LibfptrParamReportType, IFptr.LibfptrRtX);
                if (fptr.Report() != 0)
                {
                    ShowAlertAsync("Ошибка при X отчете",
                        string.Format("Ошибка при X отчете.\nОшибка {0}: {1}", fptr.ErrorCode(), fptr.ErrorDescription())).Wait();
                    return FiscalPrintResult.Fail(fptr.ErrorCode(), fptr.ErrorDescription());
                }
                return FiscalPrintResult.Ok();
            });
        }

        /// <summary>Закрытие смены, Z-отчёт (паритет reportZ).</summary>
        public static System.Threading.Tasks.Task<FiscalPrintResult> PrintZReportAsync()
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                if (!EnsureOpen(out IFptr fptr, out int code, out string desc))
                {
                    return FiscalPrintResult.Fail(code, desc);
                }
                fptr.SetParam(1021, AtolPrinterConfig.OperatorName);
                fptr.SetParam(1203, AtolPrinterConfig.OperatorInn);
                fptr.OperatorLogin();
                fptr.SetParam(IFptr.LibfptrParamReportType, IFptr.LibfptrRtCloseShift);
                if (fptr.Report() != 0)
                {
                    ShowAlertAsync("Ошибка закрытия смены",
                        string.Format("Ошибка при закрытии смены.\nОшибка {0}: {1}", fptr.ErrorCode(), fptr.ErrorDescription())).Wait();
                    return FiscalPrintResult.Fail(fptr.ErrorCode(), fptr.ErrorDescription());
                }
                return FiscalPrintResult.Ok();
            });
        }

        /// <summary>Повторная печать последнего документа (паритет print_last_document).</summary>
        public static System.Threading.Tasks.Task<FiscalPrintResult> PrintLastDocumentAsync()
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                if (!EnsureOpen(out IFptr fptr, out int code, out string desc))
                {
                    return FiscalPrintResult.Fail(code, desc);
                }
                fptr.SetParam(1021, AtolPrinterConfig.OperatorName);
                fptr.SetParam(1203, AtolPrinterConfig.OperatorInn);
                fptr.OperatorLogin();
                fptr.SetParam(IFptr.LibfptrParamReportType, IFptr.LibfptrRtLastDocument);
                if (fptr.Report() != 0)
                {
                    return FiscalPrintResult.Fail(fptr.ErrorCode(), fptr.ErrorDescription());
                }
                return FiscalPrintResult.Ok();
            });
        }

        /// <summary>Внесение наличных (паритет cashIncome).</summary>
        public static System.Threading.Tasks.Task<FiscalPrintResult> CashIncomeAsync(double sumCashIn)
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                if (!EnsureOpen(out IFptr fptr, out int code, out string desc))
                {
                    return FiscalPrintResult.Fail(code, desc);
                }
                fptr.SetParam(1021, AtolPrinterConfig.OperatorName);
                fptr.SetParam(1203, AtolPrinterConfig.OperatorInn);
                fptr.OperatorLogin();
                fptr.SetParam(IFptr.LibfptrParamSum, sumCashIn);
                if (fptr.CashIncome() != 0)
                {
                    ShowAlertAsync("Ошибка при внесении", fptr.ErrorDescription()).Wait();
                    return FiscalPrintResult.Fail(fptr.ErrorCode(), fptr.ErrorDescription());
                }
                return FiscalPrintResult.Ok();
            });
        }

        /// <summary>Инкассация / изъятие наличных (паритет cashOutcome).</summary>
        public static System.Threading.Tasks.Task<FiscalPrintResult> CashOutcomeAsync(double sumCashOut)
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                if (!EnsureOpen(out IFptr fptr, out int code, out string desc))
                {
                    return FiscalPrintResult.Fail(code, desc);
                }
                fptr.SetParam(1021, AtolPrinterConfig.OperatorName);
                fptr.SetParam(1203, AtolPrinterConfig.OperatorInn);
                fptr.OperatorLogin();
                fptr.SetParam(IFptr.LibfptrParamSum, sumCashOut);
                if (fptr.CashOutcome() != 0)
                {
                    ShowAlertAsync("Ошибка при инкасации", fptr.ErrorDescription()).Wait();
                    return FiscalPrintResult.Fail(fptr.ErrorCode(), fptr.ErrorDescription());
                }
                return FiscalPrintResult.Ok();
            });
        }

        /// <summary>Версия драйвера (паритет get_version).</summary>
        public static System.Threading.Tasks.Task<string> GetVersionAsync()
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                if (!EnsureOpen(out IFptr fptr, out int code, out string desc))
                {
                    return "Ошибка " + code + ": " + desc;
                }
                return fptr.Version();
            });
        }

        /// <summary>
        /// Тест подключения к ФР (кнопка на главной странице). Открывает связь
        /// и возвращает многострочный статус для DisplayAlert: версия драйвера,
        /// модель/серийный номер ФР, дата-время ФР, состояние смены.
        /// При неудаче EnsureOpen сам пересоздаёт драйвер и пробует второй раз.
        /// </summary>
        public static System.Threading.Tasks.Task<string> TestConnectionAsync()
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                var sb = new System.Text.StringBuilder();
                try
                {
                    // МАРКЕР СБОРКИ: по первой строке ответа всегда видно, какая
                    // версия модуля ФР реально стоит на планшете. Если этой строки
                    // на экране НЕТ — планшет работает на СТАРОЙ сборке:
                    // файл не заменился, нужна чистая пересборка.
                    sb.AppendLine("[Модуль ФР: СБОРКА 11 — настройки АТОЛ реально сохраняются в файл]");

                    // СБОРКА 9: показать падение с прошлого раза (если было).
                    string crashReport = AtolSettingsBridgeActivity.ReadAndClearCrashReport();
                    if (crashReport != "")
                    {
                        sb.AppendLine("=== ПОСЛЕДНЕЕ ПАДЕНИЕ ПРИЛОЖЕНИЯ (журнал) ===");
                        sb.AppendLine(crashReport);
                        sb.AppendLine("=== КОНЕЦ ЖУРНАЛА ПАДЕНИЙ ===");
                        sb.AppendLine();
                    }
                    // СБОРКА 10: из каких настроек сейчас работает драйвер.
                    sb.AppendLine("Настройки связи: " + DescribeSettingsSource());
                    // СБОРКА 11: что реально лежит в файле сохранённых настроек —
                    // сразу видно, дожил ли выбор «USB» из родного окна.
                    string savedJsonForReport = LoadSavedSettings();
                    if (savedJsonForReport != "")
                    {
                        string shortJson = savedJsonForReport.Length > 160
                            ? savedJsonForReport.Substring(0, 160) + "…"
                            : savedJsonForReport;
                        sb.AppendLine("Файл сохранённых настроек: " + System.IO.Path.GetFileName(SavedSettingsPath));
                        sb.AppendLine("Содержимое: " + shortJson);
                    }
                    else
                    {
                        sb.AppendLine("Файл сохранённых настроек: ещё НЕ создан (появится после");
                        sb.AppendLine("«НАСТРОЙКИ АТОЛ» -> выбрать USB -> стрелка <- в шапке окна).");
                        if (LastSettingsSaveError != "")
                        {
                            sb.AppendLine("Ошибка последней записи файла: " + LastSettingsSaveError);
                        }
                    }
                    sb.AppendLine();

                    // СНАЧАЛА диагностика USB: что планшет реально видит и у кого
                    // есть разрешение. Этот блок попадает и в успешный, и в неуспешный ответ.
                    bool frSeen = AppendUsbDiagnostics(sb);
                    sb.AppendLine();

                    // СБОРКА 7: экземпляр драйвера теперь создаёт и возвращает сам
                    // EnsureOpen (out-параметр) — вызывающий код всегда работает с
                    // ЖИВЫМ экземпляром, даже после пересозданий между попытками.
                    // fptrProbe нужен только чтобы решить, показывать ли зонд порта
                    // ДО попыток: если связь уже открыта, порт занят нами, и зонд
                    // ввёл бы в заблуждение.
                    IFptr fptrProbe = GetFptr();

                    // Зонд порта ДО первой попытки драйвера открыть связь (СБОРКА 6).
                    if (!fptrProbe.IsOpened)
                    {
                        sb.Append(BuildDirectPortProbeReport("ДО попыток драйвера"));
                        sb.AppendLine();
                    }

                    if (!EnsureOpen(out IFptr fptr, out int openCode, out string openDesc))
                    {
                        sb.AppendLine("НЕТ СВЯЗИ С ФР");
                        sb.AppendLine("Ошибка " + openCode + ": " + openDesc);

                        if (openCode == 4 && !frSeen)
                        {
                            sb.AppendLine();
                            sb.AppendLine("ВЫВОД: ФР НЕ ВИДЕН планшету как USB-устройство.");
                            sb.AppendLine("1) Убедитесь, что ФР ВКЛЮЧЕН (экран/индикатор горит).");
                            sb.AppendLine("2) Проверьте кабель (тот же, что при удачной связи) и разъём планшета.");
                            sb.AppendLine("3) Выньте кабель из планшета и вставьте обратно, глядя на экран ФР.");
                        }
                        else if (openCode == 4)
                        {
                            sb.AppendLine();
                            sb.AppendLine("ВЫВОД: ФР виден, но связь открыть не удалось.");
                            sb.AppendLine("Если у ФР в диагностике выше «Разрешение: НЕТ» —");
                            sb.AppendLine("просто нажмите кнопку ещё раз и в системном диалоге выберите «Разрешить».");
                            sb.AppendLine("Если «Разрешение: ЕСТЬ» — порт удерживает другое приложение:");
                            sb.AppendLine("1) Настройки -> Приложения -> Сервисная утилита АТОЛ -> Остановить");
                            sb.AppendLine("   (и смахнуть её из списка недавних).");
                            sb.AppendLine("2) У неё же: «Открывать по умолчанию» -> «Очистить значения по умолчанию»,");
                            sb.AppendLine("   чтобы она не перехватывала ФР при подключении кабеля.");
                            sb.AppendLine("3) Если не помогло: перезагрузите планшет при ОТКЛЮЧЁННОМ кабеле ФР,");
                            sb.AppendLine("   откройте кассу и только потом вставьте кабель ФР.");
                            sb.AppendLine("Android отдаёт порт ФР только ОДНОМУ приложению сразу.");
                            sb.AppendLine("Если утилита остановлена, а ошибка 4 осталась — нажмите кнопку «НАСТРОЙКИ АТОЛ»");
                            sb.AppendLine("внизу страницы: родное окно драйвера само сгенерирует настройки,");
                            sb.AppendLine("и мы попробуем открыть связь ими (СБОРКА 11).");
                            if (!string.IsNullOrEmpty(LastOpenAttemptsReport))
                            {
                                sb.AppendLine();
                                sb.AppendLine("ПОПЫТКИ ПОДКЛЮЧЕНИЯ (автоперебор стратегий драйвера):");
                                sb.AppendLine(LastOpenAttemptsReport);
                            }
                            sb.AppendLine();
                            sb.AppendLine("СРАВНИТЕ этот зонд с зондом ДО попыток драйвера (вверху ответа):");
                            sb.AppendLine("— ДО был свободен, ПОСЛЕ занят -> порт удерживает НАШ драйвер");
                            sb.AppendLine("  после неудачных попыток (перезапустите кассу и повторите тест);");
                            sb.AppendLine("— занят уже ДО -> порт держит ДРУГОЕ приложение/процесс;");
                            sb.AppendLine("— оба свободны, а ошибка 4 есть -> порт ни при чём: настройки драйвера");
                            sb.AppendLine("  или сам ФР (режим USB на ФР, прошивка) — тогда в поддержку АТОЛ.");
                            sb.AppendLine("  («OpenDevice() = null» в зонде ДО — это не занятость, а ещё не");
                            sb.AppendLine("   выданное USB-разрешение: дайте его и нажмите кнопку ещё раз.)");
                            sb.Append(BuildDirectPortProbeReport("ПОСЛЕ неудачных попыток драйвера"));
                            sb.AppendLine();
                            sb.AppendLine("Отчёт о запросе разрешения (текущее нажатие кнопки): " + LastUsbActionReport);
                        }
                        else
                        {
                            sb.AppendLine("(Если вы только что дали разрешение на USB в системном диалоге —");
                            sb.AppendLine("нажмите кнопку ещё раз.)");
                        }
                        AtolPrinterConfig.Log("TestConnect", sb.ToString());
                        return sb.ToString();
                    }

                    sb.AppendLine("Связь с ФР: ОК");
                    sb.AppendLine("Версия драйвера: " + fptr.Version());

                    // Короткий статус ФР: модель, версия конфигурации, серийный номер
                    fptr.SetParam(IFptr.LibfptrParamDataType, IFptr.LibfptrDtShortStatus);
                    fptr.QueryData();
                    if (fptr.ErrorCode() == 0)
                    {
                        // ВНИМАНИЕ: в биндинге libfptr10.aar НЕТ константы LibfptrParamUnitModel
                        // (CS0117), поэтому модель ФР не выводим — модель видна в шапке
                        // любого X/Z-отчёта и в сервисной утилите АТОЛ.
                        // sb.AppendLine("Модель ФР: " + (int)fptr.GetParamInt(IFptr.LibfptrParamUnitModel));
                        string serial = fptr.GetParamString(IFptr.LibfptrParamSerialNumber);
                        if (!string.IsNullOrEmpty(serial))
                        {
                            sb.AppendLine("Серийный номер: " + serial);
                        }
                    }
                    else
                    {
                        sb.AppendLine("Короткий статус ФР: ошибка " + fptr.ErrorCode() + " " + fptr.ErrorDescription());
                    }

                    // Дата/время ФР
                    fptr.SetParam(IFptr.LibfptrParamDataType, IFptr.LibfptrDtDateTime);
                    fptr.QueryData();
                    if (fptr.ErrorCode() == 0)
                    {
                        Java.Util.Date frDt = fptr.GetParamDateTime(IFptr.LibfptrParamDateTime);
                        if (frDt != null)
                        {
                            sb.AppendLine("Дата/время ФР: " + FromJavaDate(frDt).ToString("dd.MM.yyyy HH:mm:ss"));
                        }
                    }

                    // Состояние смены
                    fptr.SetParam(IFptr.LibfptrParamDataType, IFptr.LibfptrDtShiftState);
                    fptr.QueryData();
                    if (fptr.ErrorCode() == 0)
                    {
                        int state = (int)fptr.GetParamInt(IFptr.LibfptrParamShiftState);
                        if (state == IFptr.LibfptrSsOpened)
                        {
                            sb.AppendLine("Смена: открыта");
                        }
                        else if (state == IFptr.LibfptrSsClosed)
                        {
                            sb.AppendLine("Смена: закрыта");
                        }
                        else if (state == IFptr.LibfptrSsExpired)
                        {
                            sb.AppendLine("Смена: превысила 24 часа (закройте её)");
                        }
                        else
                        {
                            sb.AppendLine("Смена: код состояния " + state);
                        }
                    }

                    AtolPrinterConfig.Log("TestConnect", sb.ToString());
                    return sb.ToString();
                }
                catch (System.Exception ex)
                {
                    sb.AppendLine("НЕТ СВЯЗИ С ФР (исключение)");
                    sb.AppendLine(ex.Message);
                    AtolPrinterConfig.Log("TestConnect", sb.ToString());
                    return sb.ToString();
                }
            });
        }

        /// <summary>Состояние ресурсов ФН (паритет getFiscallInfo).</summary>
        public static System.Threading.Tasks.Task<string> GetFiscalInfoAsync()
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                if (!EnsureOpen(out IFptr fptr, out int code, out string desc))
                {
                    return "Ошибка " + code + ": " + desc;
                }

                string fnInfo = "";
                fptr.SetParam(IFptr.LibfptrParamFnDataType, IFptr.LibfptrFndtValidityDays);
                fptr.SetParam(IFptr.LibfptrParamDateTime, ToJavaDate(System.DateTime.Now));
                fptr.FnQueryData();
                uint daysRemain = (uint)fptr.GetParamInt(IFptr.LibfptrParamFnDaysRemain);
                fnInfo = "Ресурс ФН " + daysRemain + " дней ";

                fptr.SetParam(IFptr.LibfptrParamFnDataType, IFptr.LibfptrFndtFnInfo);
                fptr.FnQueryData();

                if (fptr.GetParamBool(IFptr.LibfptrParamFnNeedReplacement))
                {
                    fnInfo += "\r\n" + "Требуется срочная замена ФН !!!";
                }
                if (fptr.GetParamBool(IFptr.LibfptrParamFnResourceExhausted))
                {
                    fnInfo += "\r\n" + "Исчерпан ресурс ФН !!!";
                }
                if (fptr.GetParamBool(IFptr.LibfptrParamFnMemoryOverflow))
                {
                    fnInfo += "\r\n" + "Память ФН переполнена !!!";
                }
                if (fptr.GetParamBool(IFptr.LibfptrParamFnOfdTimeout))
                {
                    fnInfo += "\r\n" + "Превышено время ожидания ответа от ОФД !!!";
                }
                if (fptr.GetParamBool(IFptr.LibfptrParamFnCriticalError))
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
                if (!EnsureOpen(out IFptr fptr, out int code, out string desc))
                {
                    return "Ошибка " + code + ": " + desc;
                }

                fptr.SetParam(IFptr.LibfptrParamFnDataType, IFptr.LibfptrFndtOfdExchangeStatus);
                fptr.FnQueryData();

                uint unsentCount = (uint)fptr.GetParamInt(IFptr.LibfptrParamDocumentsCount);
                if (unsentCount > 0)
                {
                    Java.Util.Date dt = fptr.GetParamDateTime(IFptr.LibfptrParamDateTime);
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
                if (!EnsureOpen(out IFptr fptr, out int code, out string desc))
                {
                    return -1;
                }
                fptr.SetParam(IFptr.LibfptrParamDataType, IFptr.LibfptrDtShiftState);
                fptr.QueryData();
                int state = (int)fptr.GetParamInt(IFptr.LibfptrParamShiftState);

                if (state == IFptr.LibfptrSsExpired)
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
                if (!EnsureOpen(out IFptr fptr, out int code, out string desc))
                {
                    await ShowAlertAsync("Ошибка связи с ФР", "Не удалось подключиться к ФР:\r\n" + desc);
                    return FiscalPrintResult.Fail(code, desc);
                }

                fptr.SetParam(IFptr.LibfptrParamDataType, IFptr.LibfptrDtDateTime);
                fptr.QueryData();
                if (fptr.ErrorCode() != 0)
                {
                    await ShowAlertAsync("Проверка даты и времени в фн",
                        "При проверке даты и времени в ФН произошла ошибка\r\n" + fptr.ErrorDescription());
                    return FiscalPrintResult.Fail(fptr.ErrorCode(), fptr.ErrorDescription());
                }

                Java.Util.Date frDate = fptr.GetParamDateTime(IFptr.LibfptrParamDateTime);
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
                    fptr.SetParam(IFptr.LibfptrParamDataType, IFptr.LibfptrDtShiftState);
                    fptr.QueryData();
                    if (fptr.ErrorCode() != 0)
                    {
                        await ShowAlertAsync("Ошибка ФР",
                            "Не удалось получить состояние смены ФР:\r\n" + fptr.ErrorDescription());
                        return FiscalPrintResult.Fail(fptr.ErrorCode(), fptr.ErrorDescription());
                    }

                    int shiftState = (int)fptr.GetParamInt(IFptr.LibfptrParamShiftState);

                    if (shiftState == IFptr.LibfptrSsClosed)
                    {
                        fptr.SetParam(IFptr.LibfptrParamDateTime, ToJavaDate(System.DateTime.Now));
                        fptr.WriteDateTime();
                        if (fptr.ErrorCode() == 0)
                        {
                            AtolPrinterConfig.Log("ФР",
                                "Время на ФР автоматически скорректировано. Рассинхрон был: " +
                                minutesFr.ToString("F1") + " мин.");
                            return FiscalPrintResult.Ok();
                        }
                        AtolPrinterConfig.Log("ФР ОШИБКА",
                            "Ошибка автокорректировки времени ФР: " + fptr.ErrorDescription());
                        await ShowAlertAsync("Ошибка ФР",
                            "Не удалось автоматически скорректировать время на ФР:\r\n" + fptr.ErrorDescription());
                        return FiscalPrintResult.Fail(fptr.ErrorCode(), fptr.ErrorDescription());
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

        // Биндинг задаёт дату в ФР только как Java.Util.Date (setParam(int, Date)),
        // поэтому System.DateTime нужно конвертировать.
        private static Java.Util.Date ToJavaDate(System.DateTime localTime)
        {
            long unixMs = new System.DateTimeOffset(localTime).ToUnixTimeMilliseconds();
            return new Java.Util.Date(unixMs);
        }
    }
}