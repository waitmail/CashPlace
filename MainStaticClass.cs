using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using System.Diagnostics;
using System.IO;
using System.Net;
using Microsoft.Maui.ApplicationModel;

namespace CashPlace;

public class MainStaticClass
{
    private static string nick_shop = string.Empty;//"A05";
    private static bool _nickShopLoaded = false; // Флаг загрузки
    private static string code_shop = string.Empty;//"181cdd69-5b7b-11e4-80d6-00e081e3d824";
    private static bool _codeShopLoaded = false; // Флаг загрузки
    
    // === ПОЛЯ ДЛЯ КЭШИРОВАНИЯ ===
    private static string _lastWorkingServiceUrl = null;
    private static DateTime _lastWorkingUrlTimestamp = DateTime.MinValue;
    private static readonly object _urlLock = new object();
    private static readonly TimeSpan URL_CACHE_DURATION = TimeSpan.FromMinutes(15);

    private static string DbPath => Path.Combine(FileSystem.AppDataDirectory, "cashplace.db");
    // Единый метод для получения подключения
    public static SqliteConnection GetLocalSQLiteConnection()
    {
        return new SqliteConnection($"Data Source={DbPath}");
    }
    private static string[] _pathForWebServiceUrls = null;

    public static string[] PathForWebService
    {
        get
        {
            if (_pathForWebServiceUrls == null)
            {
                _pathForWebServiceUrls = GetPathForWebService();
            }

            return _pathForWebServiceUrls;
        }
    }
    
    public static int  CashDeskNumber
    {
        get
        {
            return 10;
        }
    }

    private static string[] GetPathForWebService()
    {
        string[] defaultUrls = new string[] { "http://8.8.8.8/DiscountSystem/Ds.asmx" };

        try
        {
            using (SqliteConnection conn = new SqliteConnection($"Data Source={DbPath}"))
            {
                conn.Open();

                string checkTableQuery = "SELECT name FROM sqlite_master WHERE type='table' AND name='constants';";
                using (SqliteCommand checkCmd = new SqliteCommand(checkTableQuery, conn))
                {
                    if (checkCmd.ExecuteScalar() == null)
                        return defaultUrls;
                }

                string query = "SELECT path_for_web_service FROM constants LIMIT 1";
                using (SqliteCommand command = new SqliteCommand(query, conn))
                using (SqliteDataReader reader = command.ExecuteReader())
                {
                    if (reader.Read() && reader[0] != DBNull.Value)
                    {
                        string jsonString = reader.GetString(0);

                        // Улучшение 1: Проверка на пустую строку перед десериализацией
                        if (!string.IsNullOrEmpty(jsonString))
                        {
                            var result = JsonConvert.DeserializeObject<string[]>(jsonString);

                            if (result != null && result.Length > 0)
                                return result;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Ошибка чтения path_for_web_service: {ex.Message}");
            ShowErrorDialog("Ошибка", $"Не удалось загрузить адреса веб-сервиса: {ex.Message}");
        }

        return defaultUrls;
    }

    public static string Nick_Shop
    {
        get
        {
            // Улучшение 3: Проверяем флаг, чтобы не дергать базу при каждом обращении
            if (!_nickShopLoaded)
            {
                _nickShopLoaded = true; // Помечаем как загруженное сразу

                try
                {
                    using (SqliteConnection conn = new SqliteConnection($"Data Source={DbPath}"))
                    {
                        conn.Open();

                        string checkTableQuery =
                            "SELECT name FROM sqlite_master WHERE type='table' AND name='constants';";
                        using (SqliteCommand checkCmd = new SqliteCommand(checkTableQuery, conn))
                        {
                            if (checkCmd.ExecuteScalar() == null)
                            {
                                return nick_shop;
                            }
                        }

                        string queryString = "SELECT nick_shop FROM constants LIMIT 1";
                        using (SqliteCommand command = new SqliteCommand(queryString, conn))
                        {
                            object result = command.ExecuteScalar();

                            if (result != null && result != DBNull.Value)
                            {
                                nick_shop = result.ToString().Trim();
                            }
                        }
                    }
                }
                catch (SqliteException ex)
                {
                    Debug.WriteLine($"Ошибка при получении названия магазина: {ex.Message}");
                    ShowErrorDialog("Ошибка БД", $"Не удалось получить название магазина: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Общая ошибка: {ex.Message}");
                    ShowErrorDialog("Ошибка", $"Произошла ошибка: {ex.Message}");
                }
            }

            return nick_shop;
        }
    }



    public static string Code_Shop
    {
        get
        {
            // Проверяем флаг, чтобы не дергать базу при каждом обращении
            if (!_codeShopLoaded)
            {
                _codeShopLoaded = true; // Помечаем как загруженное сразу

                try
                {
                    using (SqliteConnection conn = new SqliteConnection($"Data Source={DbPath}"))
                    {
                        conn.Open();

                        // 1. Проверяем, существует ли таблица
                        string checkTableQuery =
                            "SELECT name FROM sqlite_master WHERE type='table' AND name='constants';";
                        using (SqliteCommand checkCmd = new SqliteCommand(checkTableQuery, conn))
                        {
                            if (checkCmd.ExecuteScalar() == null)
                            {
                                return code_shop;
                            }
                        }

                        // 2. Читаем данные
                        string queryString = "SELECT code_shop FROM constants LIMIT 1";
                        using (SqliteCommand command = new SqliteCommand(queryString, conn))
                        {
                            object result = command.ExecuteScalar();

                            // Проверяем, что значение не равно NULL в базе данных
                            if (result != null && result != DBNull.Value)
                            {
                                code_shop = result.ToString().Trim();
                            }
                        }
                    }
                }
                catch (SqliteException ex)
                {
                    Debug.WriteLine($"Ошибка при получении кода магазина: {ex.Message}");
                    ShowErrorDialog("Ошибка БД", $"Не удалось получить код магазина: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Общая ошибка: {ex.Message}");
                    ShowErrorDialog("Ошибка", $"Произошла ошибка: {ex.Message}");
                }
            }

            return code_shop;
        }
    }

    public static string GetProductVersion()
    {
        try
        {
            string version = AppInfo.Current.VersionString;
        
            // Если версия пустая, возвращаем 0.0.0 (а не Unix время)
            if (string.IsNullOrEmpty(version))
            {
                return "0.0.0";
            }

            return version;
        }
        catch
        {
            // Если произошла ошибка, тоже возвращаем 0.0.0
            return "0.0.0";
        }
    }
    

    /// <summary>
    /// Потокобезопасный показ диалогового окна
    /// </summary>
    private static void ShowErrorDialog(string title, string message)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                if (Application.Current?.MainPage != null)
                {
                    await Application.Current.MainPage.DisplayAlert(title, message, "OK");
                }
            }
            catch (Exception ex)
            {
                // Улучшение 2: Логируем, если даже диалог не смог показаться
                Debug.WriteLine($"[{title}] {message}. Ошибка показа диалога: {ex.Message}");
            }
        });
    }

    // public static bool service_is_worker()
    // {
    //     bool result = true;
    //
    //     DS ds = MainStaticClass.get_ds();
    //
    //     // Если DS поддерживает Timeout, оставляем. 
    //     // В MAUI может потребоваться TimeSpan: ds.Timeout = TimeSpan.FromMilliseconds(5000);
    //     try
    //     {
    //         ds.Timeout = 5000;
    //         result = ds.ServiceIsWorker();
    //     }
    //     catch (Exception ex) // Добавили логирование для Android
    //     {
    //         Debug.WriteLine($"[Service] Ошибка проверки статуса: {ex.Message}");
    //         result = false;
    //     }
    //
    //     return result;
    // }
    
    public static async Task<bool> ServiceIsWorkerAsync()
    {
        // Получаем DS (это быстро, если есть кэш)
        DS ds = get_ds();
    
        try
        {
            // Запускаем синхронный запрос в фоновом потоке, чтобы не вешать UI
            return await Task.Run(() => 
            {
                ds.Timeout = 5000; // Или TimeSpan
                return ds.ServiceIsWorker();
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Service] Ошибка проверки статуса: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Создает объект DS, выбирая лучший доступный адрес из списка.
    /// </summary>
    public static DS get_ds()
    {
        DS ds = new DS();

        // 1. Проверяем кэш
        lock (_urlLock)
        {
            if (!string.IsNullOrWhiteSpace(_lastWorkingServiceUrl) &&
                DateTime.Now - _lastWorkingUrlTimestamp < URL_CACHE_DURATION)
            {
                ds.Url = _lastWorkingServiceUrl;
                return ds;
            }
        }

        // 2. Получаем список адресов
        List<string> urlsToTry = new List<string>(PathForWebService);
        if (urlsToTry.Count == 0)
        {
            urlsToTry.Add("http://8.8.8.8/DiscountSystem/Ds.asmx");
        }

        // 3. Рандомизация (Load Balancing)
        var shuffled = urlsToTry.OrderBy(x => Guid.NewGuid()).ToList();

        // 4. Перебор
        foreach (var url in shuffled)
        {
            try
            {
                // Таймаут 1500 мс
                if (IsUrlAccessible(url, 1500))
                {
                    lock (_urlLock)
                    {
                        _lastWorkingServiceUrl = url;
                        _lastWorkingUrlTimestamp = DateTime.Now;
                    }

                    ds.Url = url;
                    Debug.WriteLine($"[WebService] ✓ Подключено к: {url}");
                    return ds;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebService] ✗ Ошибка проверки {url}: {ex.Message}");
            }
        }

        // 5. Фолбэк
        ds.Url = urlsToTry.FirstOrDefault();
        Debug.WriteLine($"[WebService] ⚠ Все адреса недоступны. Фолбэк: {ds.Url}");
        return ds;
    }
    
    /// <summary>
    /// СИНХРОННАЯ проверка доступности URL для Linux и .NET 4.0+
    /// Использует HttpWebRequest, который надежно обрабатывает таймауты.
    /// </summary>
    private static bool IsUrlAccessible(string url, int timeoutMs)
    {
        HttpWebRequest request = null;
        HttpWebResponse response = null;

        try
        {
            request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = timeoutMs; // Строгий таймаут
            request.Method = "HEAD"; // Самый легкий метод
            request.AllowAutoRedirect = true;
            request.ReadWriteTimeout = timeoutMs;

            // Игнорируем ошибки SSL сертификатов (если нужно)
            // В .NET 4.0/Standard это делается через делегат
            request.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;

            // Выполняем запрос (блокирующий вызов, но с таймаутом)
            response = (HttpWebResponse)request.GetResponse();

            // Если дошли сюда - сервер ответил
            return true;
        }
        catch (WebException ex)
        {
            // WebExceptionStatus.ProtocolError означает, что сервер ответил (например 404, 500).
            // Это значит сервер ЖИВ, просто вернул ошибку. Для наших целей это успех.
            if (ex.Status == WebExceptionStatus.ProtocolError)
            {
                return true;
            }

            // Остальные ошибки (Timeout, ConnectFailure, NameResolutionFailure) - сервер недоступен
            return false;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (response != null)
                response.Close();
        }
    }
}