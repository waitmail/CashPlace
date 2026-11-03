// ══════════════════════════════════════════════════════════════════
//  ★ ПРОВЕРКА АКЦИЙ — обработчик кнопки ChecksPage ★
//  Полноценная partial-часть класса ChecksPage (namespace CashPlace).
//  Компилируется как отдельный файл — ничего вставлять в ChecksPage.xaml.cs
//  НЕ нужно. Если в ChecksPage.xaml.cs уже есть OnVerifyActionsClicked —
//  оставьте только ОДНУ копию (удалите дубликат).
//  Открывает VerifyActionsPage — планшетный аналог десктопного окна
//  VerifyActions (кнопка btn_check_actions из списка чеков).
// ══════════════════════════════════════════════════════════════════

using System;
using System.Threading.Tasks;

namespace CashPlace
{
    public partial class ChecksPage
    {
        // ★ Проверка акций: черновой подбор товаров и просмотр сработавших акций.
        //   Расчёт идёт ТОЛЬКО на копиях строк — ничего не записывается в чеки.
        private async void OnVerifyActionsClicked(object sender, EventArgs e)
        {
            try
            {
                await Navigation.PushAsync(new VerifyActionsPage());
            }
            catch (Exception ex)
            {
                await DisplayAlert("Ошибка", ex.Message, "OK");
            }
        }
    }
}