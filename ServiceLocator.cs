using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CashPlace
{
    public static class ServiceLocator
    {
        private static readonly TimeSpan URL_CACHE_DURATION = TimeSpan.FromMinutes(5);
        private static string _lastWorkingServiceUrl;
        private static DateTime _lastWorkingUrlTimestamp;

        // Меняем lock на SemaphoreSlim для асинхронной блокировки
        private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        // Добавьте using System.Net; в самом верху файла, если его нет!
        private static readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler
        {
            Proxy = System.Net.WebRequest.DefaultWebProxy,
            UseProxy = true,
            DefaultProxyCredentials = System.Net.CredentialCache.DefaultNetworkCredentials,
            PreAuthenticate = true
        })
        {
            Timeout = TimeSpan.FromMilliseconds(5000)
        };

        public static async Task<DS> DsAsync()
        {
            // ConfigureStart = false говорит: "если ты закончил, продолжай в любом свободном потоке, не ищи UI"
            //System.Diagnostics.Debugger.Break();
            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!string.IsNullOrWhiteSpace(_lastWorkingServiceUrl) &&
                    DateTime.Now - _lastWorkingUrlTimestamp < URL_CACHE_DURATION)
                {
                    DS ds = new DS();
                    ds.Url = _lastWorkingServiceUrl;
                    return ds;
                }

                DS resultDs = new DS();

                List<string> urlsToTry = new List<string>(MainStaticClass.PathForWebService);
                if (urlsToTry.Count == 0)
                {
                    urlsToTry.Add("http://8.8.8.8/DiscountSystem/Ds.asmx");
                }

                var shuffled = urlsToTry.OrderBy(_ => Random.Shared.Next()).ToList();

                using var cts = new CancellationTokenSource();
                var probeTasks = shuffled.Select(url => ProbeUrlAsync(url, cts.Token)).ToList();

                while (probeTasks.Count > 0)
                {
                    var completedTask = await Task.WhenAny(probeTasks).ConfigureAwait(false);
                    probeTasks.Remove(completedTask);

                    try
                    {
                        string successfulUrl = await completedTask.ConfigureAwait(false); // И ЗДЕСЬ
                        if (successfulUrl != null)
                        {
                            cts.Cancel();

                            _lastWorkingServiceUrl = successfulUrl;
                            _lastWorkingUrlTimestamp = DateTime.Now;

                            resultDs.Url = successfulUrl;
                            Console.WriteLine($"[WebService] ✓ Подключено к: {successfulUrl}");

                            // ==========================================
                            // МОСТ: Прогреваем HttpWebRequest для будущего SOAP-запроса
                            // ==========================================
                            try
                            {
                                var warmupReq = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(successfulUrl + "?w=1");
                                warmupReq.Proxy = System.Net.WebRequest.DefaultWebProxy;
                                warmupReq.Timeout = 3000; // Ждем максимум 3 секунды
                                warmupReq.GetResponse()?.Close(); // Отправили и сразу закрыли
                            }
                            catch { /* Игнорируем ошибки прогрева */ }
                            // ==========================================

                            return resultDs;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                resultDs.Url = shuffled.FirstOrDefault();
                Console.WriteLine($"[WebService] ⚠ Все адреса недоступны. Фолбэк: {resultDs.Url}");
                return resultDs;
            }
            finally
            {
                _semaphore.Release();
            }
        }
        
        private static string Version()
        {
            try
            {
                // AppInfo.Current.VersionString возвращает версию из AndroidManifest.xml 
                // (или из .csproj файла, например "1.0.0")
                string version = AppInfo.Current.VersionString;

                // Если версия по какой-то причине пустая, возвращаем Unix-время как фоллбэк
                if (string.IsNullOrEmpty(version))
                {
                    return DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                }

                return version;
            }
            catch
            {
                // Если произошла какая-то ошибка при доступе к AppInfo
                return DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            }
        }

        private static async Task<string> ProbeUrlAsync(string url, CancellationToken ct)
        {
            try
            {
                // И ЗДЕСЬ
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                return response.IsSuccessStatusCode ? url : null;
            }
            catch
            {
                return null;
            }
        }

        public static async Task ResetDsCacheAsync()
        {
            // Асинхронно ждем 5 секунд, не блокируя UI-поток
            if (await _semaphore.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                try
                {
                    _lastWorkingServiceUrl = null;
                    // DateTime.MinValue сбрасывать не обязательно, так как 
                    // проверка !string.IsNullOrWhiteSpace(_lastWorkingServiceUrl) 
                    // сразу оборвет условие из-за null, но это не вредит.
                    Console.WriteLine("[WebService] ♻ Кэш соединений сброшен");
                }
                finally
                {
                    _semaphore.Release();
                }
            }
            else
            {
                Console.WriteLine("[WebService] ⚠ Не удалось сбросить кэш: семафор занят слишком долго");
            }
        }
    }
}