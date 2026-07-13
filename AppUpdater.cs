using System;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;

#if ANDROID
using Android.Content;
using Android.App;
using Application = Android.App.Application;
#endif

namespace CashPlace;

public class AppUpdater
{
    // Метод 1: Проверяет сервер, скачивает APK (если нужно) и возвращает true, если обновление готово к установке
    public static async Task<bool?> CheckAndDownloadUpdateAsync(IProgress<string> progress = null)
    {
        try
        {
            DS ds = await ServiceLocator.DsAsync();
            ds.Timeout = 100000;

            string nick_shop = MainStaticClass.Nick_Shop?.Trim();
            string code_shop = MainStaticClass.Code_Shop?.Trim();
            if (string.IsNullOrEmpty(nick_shop) || string.IsNullOrEmpty(code_shop)) return false;

            string count_day = CryptorEngine.GetCountDay();
            string key = nick_shop + count_day + code_shop;
            string local_version = MainStaticClass.GetProductVersion().Replace(".", "");

            // Формируем запрос: code_shop | версия | code_shop
            string data = code_shop + "|" + local_version + "|" + code_shop;
            string encrypted_data = CryptorEngine.Encrypt(data, true, key);

            // ★ СООБЩАЕМ UI, ЧТО ИДЕМ ЗА ПОДТВЕРЖДЕНИЕМ НА СЕРВЕР ★
            progress?.Report("Запрос к серверу...");

            // Вызываем метод вашего веб-сервиса
            string encrypted_response = await Task.Run(() =>
                ds.GetUpdateProgramAndroid(nick_shop, encrypted_data, "10")
            );

            // Если ответ пустой - значит ошибка авторизации или файл не найден на сервере
            if (string.IsNullOrEmpty(encrypted_response)) return null;

            string decrypted_response = CryptorEngine.Decrypt(encrypted_response, true, key);
            string[] parts = decrypted_response.Split('|', 2);
            if (parts.Length < 2) return null;

            string server_version = parts[0].Replace(".", "");
            string base64_file = parts[1];

            if (!long.TryParse(local_version, out long local_ver) || !long.TryParse(server_version, out long server_ver))
                return false;
            
            // Если серверная версия не новее - выходим (возвращаем false)
            if (server_ver <= local_ver)
            {
#if ANDROID
                string oldApkPath = Path.Combine(Application.Context.CacheDir.AbsolutePath, "update.apk");
                string oldVerPath = Path.Combine(Application.Context.CacheDir.AbsolutePath, "downloaded_ver.txt");
                if (File.Exists(oldApkPath)) File.Delete(oldApkPath);
                if (File.Exists(oldVerPath)) File.Delete(oldVerPath);
#endif
                return false; // Обновлений нет
            }

#if ANDROID
            string tempApkPath = Path.Combine(Application.Context.CacheDir.AbsolutePath, "update.apk");
            string downloadedVerPath = Path.Combine(Application.Context.CacheDir.AbsolutePath, "downloaded_ver.txt");

            // Если мы уже скачали эту версию ранее (но не установили), не качаем повторно!
            if (File.Exists(downloadedVerPath) && File.Exists(tempApkPath))
            {
                string downloadedVer = File.ReadAllText(downloadedVerPath).Replace(".", "");
                if (downloadedVer == server_version) return true;
            }

            // ★ СЕРВЕР ПРИСЛАЛ ФАЙЛ, НАЧИНАЕМ ИЗВЛЕКАТЬ И СОХРАНЯТЬ ЕГО ★
            progress?.Report("Скачивание APK...");

            // Декодируем Base64 в байты APK файла
            byte[] apkBytes = Convert.FromBase64String(base64_file);
            if (apkBytes.Length < 1024) return false;

            // Сохраняем APK в кэш
            await File.WriteAllBytesAsync(tempApkPath, apkBytes);
            
            File.WriteAllText(downloadedVerPath, parts[0]); // Запоминаем скачанную версию

            return true; // Файл скачан, готов к установке
#else
             return false; // Для Windows возвращаем false
 #endif
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppUpdater] Ошибка: {ex.Message}");
            // ★ Если упало исключение (нет сети, таймаут) - возвращаем null (Ошибка)
            return null; 
        }
    }

    // Метод 2: Запускает системный установщик Android (по нажатию кнопки)
    public static void InstallDownloadedUpdate()
    {
#if ANDROID
        try
        {
            string tempFilePath = Path.Combine(Application.Context.CacheDir.AbsolutePath, "update.apk");
            if (!File.Exists(tempFilePath)) return;

            var file = new Java.IO.File(tempFilePath);
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                Application.Context, 
                Application.Context.PackageName + ".fileprovider", 
                file
            );

            Intent intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, "application/vnd.android.package-archive");
            intent.SetFlags(ActivityFlags.NewTask);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            
            Application.Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppUpdater] Ошибка установки: {ex.Message}");
        }
#endif
    }
}