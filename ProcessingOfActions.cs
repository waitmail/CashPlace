using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace CashPlace.Services.Actions
{
    // ══════════════════════════════════════════════════════════════════
    //  МОДЕЛИ ДАННЫХ (при желании выносятся в ActionModels.cs)
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Рабочая строка чека для расчёта акций — аналог DataRow десктопного DataTable.
    /// Перед вызовом движка мапьте свою модель товаров в этот класс,
    /// после — забирайте result.Lines обратно.
    /// </summary>
    public class CheckLine
    {
        public long TovarCode { get; set; }
        public string TovarName { get; set; } = "";
        public decimal Quantity { get; set; }
        public decimal Price { get; set; }               // цена без скидки
        public decimal PriceAtDiscount { get; set; }     // цена со скидкой
        public decimal SumFull { get; set; }             // сумма без скидки
        public decimal SumAtDiscount { get; set; }       // сумма со скидкой
        public int Action { get; set; }                  // № док. акции, давшей скидку
        public int Gift { get; set; }                    // № док. акции, давшей подарок
        public int Action2 { get; set; }                 // >0 — позиция занята акцией
        public string Marking { get; set; } = "0";       // код маркировки ("0" — обычный)
        public string PromoDescription { get; set; } = ""; // текст акции в строку чека
    }

    /// <summary>Шапка акционного документа (action_header).</summary>
    public class ActionHeaderInfo
    {
        public int NumDoc { get; set; }
        public int Tip { get; set; }
        public int Kind { get; set; }              // 0 обычная, 1 по штрихкоду, 2 персональная
        public int ExecutionOrder { get; set; }    // порядок применения
        public int Marker { get; set; }            // 1 — ввод ШК подарка при сработке
        public int TimeStart { get; set; }         // минуты от начала суток
        public int TimeEnd { get; set; }
        public string DateStartedRaw { get; set; } = "";
        public string DateEndRaw { get; set; } = "";
        public DateTime? DateStarted { get; set; }
        public DateTime? DateEnd { get; set; }
        public decimal Persent { get; set; }
        public decimal Sum { get; set; }
        public decimal Sum1 { get; set; }
        public decimal GiftPrice { get; set; }     // цена подарка (0 = бесплатно)
        public string Comment { get; set; } = "";
        public string Barcode { get; set; } = "";
        public bool BonusPromotion { get; set; }
    }

    /// <summary>Строка табличной части акции (action_table).</summary>
    public class ActionTablePosition
    {
        public int NumDoc { get; set; }
        public long CodeTovar { get; set; }
        public int NumList { get; set; }           // № списка; в 13 типе — порог количества
        public decimal Price { get; set; }         // в 1 типе — акционная цена, в 13 — градиентная
    }

    /// <summary>Итог расчёта акций.</summary>
    public class ActionCalcResult
    {
        public List<CheckLine> Lines { get; } = new List<CheckLine>();
        /// <summary>Сообщения кассиру (замена каскада окон десктопа) — показать одним списком.</summary>
        public List<string> Messages { get; } = new List<string>();
        /// <summary>№ документов акций 10 типа (купонная кратность) — уходит в печать купонов.</summary>
        public List<int> CouponActions { get; } = new List<int>();
        public bool HaveAction { get; set; }
        public decimal TotalFull => Lines.Sum(l => l.SumFull);
        public decimal TotalAtDiscount => Lines.Sum(l => l.SumAtDiscount);
    }

    // ══════════════════════════════════════════════════════════════════
    //  ДВИЖОК ОБРАБОТКИ АКЦИЙ (аналог десктопного ProcessingOfActions)
    //  Отличия от десктопа:
    //   - PostgreSQL/Npgsql → локальный SQLite;
    //   - DataTable → List<CheckLine>;
    //   - временные таблицы БД (checks_table_temp, table12, tovar_action,
    //     roll_up_temp) → коллекции в памяти;
    //   - MessageBoxHelper.Show → накопление сообщений в result.Messages;
    //   - ShowQueryWindowBarcode (marker=1) → сообщение-подсказка (v1);
    //   - один финальный расчёт при оплате вместо пересчёта на каждом товаре.
    // ══════════════════════════════════════════════════════════════════
    public class ProcessingOfActions
    {
        /// <summary>Минимальная цена после скидки, чтобы не обнулилась (паритет с десктопом).</summary>
        private const decimal MinPriceAfterDiscount = 0.01m;

        private readonly SqliteConnection _db;
        private readonly ActionCalcResult _result = new ActionCalcResult();
        private List<CheckLine> _lines = new List<CheckLine>();
        private string _clientCode = "";

        /// <summary>Активные шапки, отсортированы execution_order ASC, tip ASC.</summary>
        private List<ActionHeaderInfo> _activeHeaders = new List<ActionHeaderInfo>();

        /// <summary>action_table только активных акций: num_doc → строки.</summary>
        private readonly Dictionary<int, List<ActionTablePosition>> _positions = new Dictionary<int, List<ActionTablePosition>>();

        /// <summary>num_doc → множество кодов товаров акции (все списки).</summary>
        private readonly Dictionary<int, HashSet<long>> _codesOfDoc = new Dictionary<int, HashSet<long>>();

        public ProcessingOfActions(SqliteConnection openConnection)
        {
            _db = openConnection ?? throw new ArgumentNullException(nameof(openConnection));
        }

        // ────────────────────────────────────────────────────────────
        //  ТОЧКА ВХОДА. Вызывать из CheckPage.OnPayClicked ПЕРЕД
        //  вычислением итоговой суммы и переходом на форму оплаты.
        //  products   — текущая табличная часть чека;
        //  clientCode — код клиента (для kind=2 и tip=9) или "";
        //  actionBarcodes — отсканированные акционные ШК (kind=1) или пусто.
        // ────────────────────────────────────────────────────────────
        public Task<ActionCalcResult> ProcessAsync(List<CheckLine> products, string clientCode, List<string> actionBarcodes)
        {
            return Task.FromResult(Process(products, clientCode, actionBarcodes));
        }

        public ActionCalcResult Process(List<CheckLine> products, string clientCode, List<string> actionBarcodes)
        {
            _lines = (products ?? new List<CheckLine>()).Select(CloneLine).ToList(); // работаем на копии
            _clientCode = (clientCode ?? "").Trim();

            DateTime now = DateTime.Now;
            int minutes = (int)(now - now.Date).TotalMinutes;

            LoadActiveActions(now.Date, minutes);
            var checkCodes = _lines.Select(l => l.TovarCode).ToHashSet();

            // 1) kind=1 — акции по акционным штрихкодам (tips 1,2,3,4,5,8).
            //    Паритет с десктопом: здесь НЕ проверяется время действия.
            foreach (string bc in actionBarcodes ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(bc)) continue;
                string b = bc.Trim();
                foreach (var h in _activeHeaders.Where(h => h.Kind == 1 && h.Barcode.Trim() == b))
                    Apply(h);
            }

            // 2) kind=2 — персональные акции клиента (tips 1,2,3,4).
            if (_clientCode != "")
            {
                var personalDocs = LoadPersonalDocNums(_clientCode);
                if (personalDocs.Count > 0)
                {
                    foreach (var h in _activeHeaders.Where(h => h.Kind == 2
                                                                && h.Barcode.Trim().Length == 0
                                                                && personalDocs.Contains(h.NumDoc)
                                                                && PositionsIntersectCheck(h.NumDoc, checkCodes)))
                        Apply(h);
                }
            }

            // 3) kind=0 — обычные акции (tips 1,2,3,4,6,8,9,12,13; tip=10 — отдельным проходом).
            foreach (var h in _activeHeaders.Where(h => h.Kind == 0 && h.Tip != 10 && h.Barcode.Trim().Length == 0))
                Apply(h);

            // 4) tip=10 — купонная кратность (цены не меняет).
            CheckAction10();

            // 5) Склейка одинаковых строк после раздвоений (аналог roll_up_dt).
            _result.Lines.AddRange(RollUp(_lines));
            return _result;
        }

        // ────────────────────────────────────────────────────────────
        //  ДИСПЕТЧЕР
        // ────────────────────────────────────────────────────────────
        private void Apply(ActionHeaderInfo h)
        {
            try
            {
                switch (h.Tip)
                {
                    case 1:
                        if (h.Persent != 0) Action1Discount(h); else Action1Gift(h);
                        break;
                    case 2:
                        if (h.Persent != 0) Action2Discount(h); else Action2Gift(h);
                        break;
                    case 3:
                        // у персональных (kind=2) подарочная ветка 3 типа не вызывается — паритет с десктопом
                        if (h.Persent != 0) Action3Discount(h); else if (h.Kind != 2) Action3Gift(h);
                        break;
                    case 4:
                        if (h.Persent != 0) Action4Discount(h); else if (h.Kind != 2) Action4Gift(h);
                        break;
                    case 5:
                        if (h.Kind == 1) Action5(h); // фикс. сумма скидки — только по акционному ШК
                        break;
                    case 6: Action6(h); break;
                    case 8:
                        if (h.Persent != 0) Action8Discount(h); else Action8Gift(h);
                        break;
                    case 9:
                        // день рождения: у клиента день рождения ±1 день → логика 1 типа (процентная)
                        if (h.Persent != 0 && IsBirthdayClient()) Action1Discount(h);
                        break;
                    case 12: Action12(h); break;
                    case 13: Action13(h); break;
                    default:
                        Msg("Неопознанный тип акции в документе № " + h.NumDoc);
                        break;
                }
            }
            catch (Exception ex)
            {
                Msg("Ошибка при обработке акции № " + h.NumDoc + " (тип " + h.Tip + "): " + ex.Message);
            }
        }

        // ════════════════════════════════════════════════════════════
        //  ТИП 1. Скидка или подарок на конкретный товар из списка.
        // ════════════════════════════════════════════════════════════

        /// <summary>persent≠0: если в action_table задана цена — она, иначе цена×(1−persent/100).</summary>
        private void Action1Discount(ActionHeaderInfo h)
        {
            var prices = PositionPrices(h.NumDoc); // код → цена (при дублях побеждает последняя — как на десктопе)
            bool fired = false;
            foreach (var l in _lines)
            {
                if (l.Action2 > 0 || !IsPriceValidForAction(l, h.Persent)) continue;
                if (!prices.TryGetValue(l.TovarCode, out decimal actionPrice)) continue;

                decimal priceAtDiscount = actionPrice != 0
                    ? actionPrice
                    : Math.Round(l.Price * (1m - h.Persent / 100m), 2, MidpointRounding.AwayFromZero);

                l.PriceAtDiscount = priceAtDiscount;
                l.SumFull = Math.Round(l.Quantity * l.Price, 2, MidpointRounding.AwayFromZero);
                l.SumAtDiscount = Math.Round(l.Quantity * priceAtDiscount, 2, MidpointRounding.AwayFromZero);
                l.Action = h.NumDoc;
                l.Action2 = h.NumDoc;
                l.PromoDescription = h.Comment;
                fired = true;
            }
            if (fired) _result.HaveAction = true;
        }

        /// <summary>persent=0: пометка подарка + сообщение кассиру. Позиция НЕ занимается (паритет: action2 не ставится).</summary>
        private void Action1Gift(ActionHeaderInfo h)
        {
            var codes = CodesOfDoc(h.NumDoc);
            foreach (var l in _lines)
            {
                if (l.Action2 > 0 || !IsPriceValidForAction(l)) continue;
                if (!codes.Contains(l.TovarCode)) continue;

                _result.HaveAction = true;
                l.Gift = h.NumDoc;
                Msg("Сработала акция, НЕОБХОДИМО выдать подарок " + h.Comment);
                if (h.Marker == 1)
                    Msg("Акция № " + h.NumDoc + ": требуется ввод штрихкода подарка (marker=1).");
            }
        }

        // ════════════════════════════════════════════════════════════
        //  ТИП 2. Комплект «список 1 + список 2».
        // ════════════════════════════════════════════════════════════

        /// <summary>persent≠0: скидка на min(штук сп.1, штук сп.2) позиций списка 1; список 2 не дешевеет.</summary>
        private void Action2Discount(ActionHeaderInfo h)
        {
            var byList = PositionsByList(h.NumDoc);
            if (!byList.TryGetValue(1, out var list1) || !byList.TryGetValue(2, out var list2))
            {
                Msg("Отсутствует первый или второй список товаров.\r\nНомер акции " + h.NumDoc);
                return;
            }

            // Этап 1: собираем штучные количества по спискам.
            // Список 1 — только позиции с ценой, которая не занулится скидкой; список 2 — без фильтра.
            var available1 = new Dictionary<long, int>();
            var available2 = new Dictionary<long, int>();
            foreach (var l in _lines)
            {
                if (l.Action2 > 0) continue;
                int qty = (int)Math.Floor(l.Quantity);
                if (qty < 1) continue; // весовые позиции не участвуют

                bool in1 = list1.Contains(l.TovarCode);
                bool in2 = list2.Contains(l.TovarCode);
                if (!in1 && !in2) continue;

                if (in1 && IsPriceValidForAction(l, h.Persent))
                    available1[l.TovarCode] = available1.GetValueOrDefault(l.TovarCode) + qty;
                if (in2)
                    available2[l.TovarCode] = available2.GetValueOrDefault(l.TovarCode) + qty;
            }

            // Этап 2: число полных комплектов.
            int kits = Math.Min(available1.Values.Sum(), available2.Values.Sum());
            if (kits == 0) return;

            // Этап 3: выдаём скидку позициям списка 1 в пределах лимита.
            int applied = 0;
            var rowsToAdd = new List<CheckLine>();
            foreach (var l in _lines)
            {
                if (applied >= kits) break;
                if (l.Action2 > 0) continue;
                int qty = (int)Math.Floor(l.Quantity);
                if (qty < 1 || !list1.Contains(l.TovarCode)) continue;
                if (!IsPriceValidForAction(l, h.Persent)) continue;

                int canDiscount = Math.Min(qty, kits - applied);
                if (canDiscount <= 0) continue;

                if (qty <= canDiscount)
                {
                    ApplyPercentToLine(l, h.Persent, h.NumDoc);
                    applied += qty;
                }
                else
                {
                    // раздваиваем: canDiscount шт. со скидкой, остаток — без
                    var discounted = CloneLine(l);
                    discounted.Quantity = canDiscount;
                    ApplyPercentToLine(discounted, h.Persent, h.NumDoc);
                    rowsToAdd.Add(discounted);

                    decimal remainingQty = l.Quantity - canDiscount;
                    l.Quantity = remainingQty;
                    l.SumFull = Math.Round(remainingQty * l.Price, 2, MidpointRounding.AwayFromZero);
                    l.SumAtDiscount = Math.Round(remainingQty * l.PriceAtDiscount, 2, MidpointRounding.AwayFromZero);
                    applied += canDiscount;
                }
            }
            _lines.AddRange(rowsToAdd);

            if (applied > 0)
            {
                _result.HaveAction = true;
                MarkActionTovar(h);
            }
        }

        /// <summary>persent=0: подарки. Число комплектов = min количеств по условным спискам 2..N; товары списка 1 отдаются по gift_price.</summary>
        private void Action2Gift(ActionHeaderInfo h)
        {
            var byList = PositionsByList(h.NumDoc);
            if (!byList.TryGetValue(1, out var giftList))
            {
                Msg("Первый список товаров (подарочный) отсутствует. Акция № " + h.NumDoc);
                return;
            }

            // 1. Сброс разметки этой акции (повторный расчёт считает заново).
            foreach (var l in _lines)
            {
                bool giftOfThis = l.Gift == h.NumDoc;
                if (giftOfThis)
                {
                    l.PriceAtDiscount = l.Price;
                    l.SumFull = l.Price * l.Quantity;
                    l.SumAtDiscount = l.Price * l.Quantity;
                    l.Gift = 0;
                    l.Action = 0;
                }
                if (giftOfThis || l.Action2 == h.NumDoc) l.Action2 = 0;
            }

            // 2. Комплекты: min количеств по условным спискам 2..N.
            var condLists = byList.Keys.Where(k => k != 1).ToList();
            var counts = condLists.ToDictionary(n => n, n => 0);
            foreach (var l in _lines)
            {
                if (giftList.Contains(l.TovarCode)) continue;       // подарочный товар комплект не создаёт
                if (l.Action2 > 0 && l.Action2 != h.NumDoc) continue; // строка занята другой акцией
                if (!IsPriceValidForAction(l)) continue;

                int qty = (int)decimal.Truncate(l.Quantity);
                foreach (int n in condLists)
                    if (byList[n].Contains(l.TovarCode)) counts[n] += qty;
            }
            // паритет: на десктопе при отсутствии списков 2..N Min() падал в ошибку — здесь аккуратно 0
            int giftCount = condLists.Count > 0 ? counts.Values.Min() : 0;
            if (giftCount <= 0)
            {
                var anyGiftRows = _lines.Any(l => giftList.Contains(l.TovarCode)
                                                  && (l.Gift == 0 || l.Gift == h.NumDoc)
                                                  && (l.Action2 == 0 || l.Action2 == h.NumDoc));
                if (anyGiftRows)
                    Msg("Позиция из подарочного списка остаётся по полной цене: комплект для акции не собран. " + h.Comment);
                return;
            }

            _result.HaveAction = true;

            // 3. Подарочные строки — товары списка 1, уже пробитые в чек.
            var giftRows = _lines.Where(l => giftList.Contains(l.TovarCode)
                                          && (l.Gift == 0 || l.Gift == h.NumDoc)
                                          && (l.Action2 == 0 || l.Action2 == h.NumDoc)).ToList();
            if (giftRows.Count == 0) return; // комплект собран, но подарочного товара нет

            // 4. Применение: дарим не более giftCount шт, излишек — по полной цене.
            decimal giftPrice = h.GiftPrice;
            decimal remaining = giftCount;
            bool overflowWarned = false;
            foreach (var g in giftRows)
            {
                if (remaining <= 0)
                {
                    if (!overflowWarned)
                    {
                        Msg("По акции положено подарков: " + giftCount + " шт. Остальные позиции из списка 1 — по полной цене.");
                        overflowWarned = true;
                    }
                    break;
                }

                decimal qty = g.Quantity;
                if (qty > remaining)
                {
                    // раздваиваем: remaining — подарок, остаток — полная цена
                    var rest = CloneLine(g);
                    rest.Quantity = qty - remaining;
                    rest.SumFull = g.Price * rest.Quantity;
                    rest.SumAtDiscount = g.Price * rest.Quantity;
                    rest.Gift = 0;
                    rest.Action = 0;
                    rest.Action2 = 0;
                    _lines.Add(rest);

                    g.Quantity = remaining;
                    qty = remaining;
                }

                g.PriceAtDiscount = giftPrice;
                g.SumFull = g.Price * qty;
                g.SumAtDiscount = giftPrice * qty;
                g.Action = 0;               // у строки подарка признак скидочной акции = 0 (эталон десктопа)
                g.Gift = h.NumDoc;
                g.Action2 = h.NumDoc;
                g.PromoDescription = h.Comment;
                remaining -= qty;
            }

            Msg("Сработала акция: подарок выдан (комплектов " + giftCount + "). " + h.Comment);
            MarkActionTovar(h);
        }

        // ════════════════════════════════════════════════════════════
        //  ТИП 3. Порог по сумме: сумма позиций списка ≥ sum.
        // ════════════════════════════════════════════════════════════

        /// <summary>persent≠0: скидка на все позиции списка. Порог считается по sum_at_discount (так на десктопе).</summary>
        private void Action3Discount(ActionHeaderInfo h)
        {
            var codes = CodesOfDoc(h.NumDoc);
            decimal sumOnDoc = 0;
            foreach (var l in _lines)
            {
                if (l.Action2 > 0 || !IsPriceValidForAction(l, h.Persent)) continue;
                if (codes.Contains(l.TovarCode)) sumOnDoc += l.SumAtDiscount;
            }
            if (sumOnDoc < h.Sum) return;

            _result.HaveAction = true;
            foreach (var l in _lines)
            {
                if (l.Action2 > 0 || !IsPriceValidForAction(l, h.Persent)) continue;
                if (!codes.Contains(l.TovarCode)) continue;

                decimal priceAtDiscount = Math.Round(l.Price - l.Price * h.Persent / 100m, 2, MidpointRounding.AwayFromZero);
                l.PriceAtDiscount = priceAtDiscount;
                l.SumFull = l.Quantity * l.Price;
                l.SumAtDiscount = l.Quantity * priceAtDiscount;
                l.Action = h.NumDoc;
                l.Action2 = h.NumDoc;
            }
        }

        /// <summary>persent=0: сообщение о подарке, цены не меняются, позиции не занимаются.</summary>
        private void Action3Gift(ActionHeaderInfo h)
        {
            var codes = CodesOfDoc(h.NumDoc);
            decimal sumOnDoc = 0;
            foreach (var l in _lines)
            {
                if (l.Action2 > 0 || !IsPriceValidForAction(l)) continue;
                if (codes.Contains(l.TovarCode)) sumOnDoc += l.SumAtDiscount;
            }
            if (sumOnDoc < h.Sum) return;

            _result.HaveAction = true;
            Msg(h.Comment);
            if (h.Marker == 1)
                Msg("Акция № " + h.NumDoc + ": требуется ввод штрихкода подарка (marker=1).");
        }

        // ════════════════════════════════════════════════════════════
        //  ТИП 4. «Каждый N-й дешевле / в подарок».
        // ════════════════════════════════════════════════════════════

        /// <summary>persent≠0: штуки сортируются по цене ↑, каждая полная группа из sum шт. — скидка на самую дешёвую в группе.</summary>
        private void Action4Discount(ActionHeaderInfo h)
        {
            int groupSize = (int)h.Sum;
            if (groupSize <= 0) return;

            var codes = CodesOfDoc(h.NumDoc);
            var untouched = new List<CheckLine>();
            var pieces = new List<Piece>();
            foreach (var l in _lines)
            {
                if (l.Action2 > 0 || !IsPriceValidForAction(l, h.Persent) || !codes.Contains(l.TovarCode))
                {
                    untouched.Add(l);
                    continue;
                }
                // десктоп бросал исключение на дробном количестве — здесь пропускаем строку
                if (l.Quantity != Math.Floor(l.Quantity))
                {
                    untouched.Add(l);
                    continue;
                }
                for (int i = 0; i < (int)l.Quantity; i++)
                    pieces.Add(new Piece { Code = l.TovarCode, Name = l.TovarName, Price = l.Price, Marking = l.Marking });
            }

            if (pieces.Count < groupSize) return; // ни одной полной группы

            pieces.Sort((a, b) => a.Price.CompareTo(b.Price)); // самые дешёвые первыми

            bool anyDiscount = false;
            var groups = new List<ListGroup>();
            for (int i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                bool hasDiscount = (i % groupSize == 0) && (i + groupSize <= pieces.Count);
                if (hasDiscount) anyDiscount = true;

                decimal finalPrice = hasDiscount
                    ? Math.Round(p.Price - p.Price * h.Persent / 100m, 2, MidpointRounding.AwayFromZero)
                    : p.Price;

                // маркированные штуки не склеиваются (каждая — уникальный физический объект)
                ListGroup g = (p.Marking != "0")
                    ? null
                    : groups.FirstOrDefault(x => !x.IsMarked && x.Code == p.Code && x.Price == p.Price && x.FinalPrice == finalPrice);

                if (g == null)
                {
                    g = new ListGroup { Code = p.Code, Name = p.Name, Price = p.Price, FinalPrice = finalPrice,
                                        Action = hasDiscount ? h.NumDoc : 0, IsMarked = p.Marking != "0", Marking = p.Marking };
                    groups.Add(g);
                }
                g.Count += 1m;
                g.SumFull += p.Price;
                g.SumDiscount += finalPrice;
            }

            // пересборка чека: сначала акционные группы, затем нетронутые строки
            var rebuilt = new List<CheckLine>();
            foreach (var g in groups)
            {
                rebuilt.Add(new CheckLine
                {
                    TovarCode = g.Code,
                    TovarName = g.Name,
                    Quantity = g.Count,
                    Price = g.Price,
                    PriceAtDiscount = g.FinalPrice,
                    SumFull = g.SumFull,
                    SumAtDiscount = g.SumDiscount,
                    Action = g.Action,
                    Gift = 0,
                    Action2 = g.Action,
                    Marking = g.Marking
                });
            }
            rebuilt.AddRange(untouched);
            _lines = rebuilt;

            if (anyDiscount)
            {
                _result.HaveAction = true;
                MarkActionTovar(h);
            }
        }

        /// <summary>persent=0: каждая sum-я (самая дешёвая в группе) штука — подарок по gift_price; все позиции занимаются (action2).</summary>
        private void Action4Gift(ActionHeaderInfo h)
        {
            int groupSize = (int)h.Sum;
            if (groupSize <= 0) return;

            var codes = CodesOfDoc(h.NumDoc);
            var untouched = new List<CheckLine>();
            var pieces = new List<Piece>();
            decimal quantityOnDoc = 0;
            foreach (var l in _lines)
            {
                if (l.Action2 > 0 || !IsPriceValidForAction(l) || !codes.Contains(l.TovarCode))
                {
                    untouched.Add(l);
                    continue;
                }
                int n = (int)decimal.Truncate(l.Quantity);
                for (int i = 0; i < n; i++)
                    pieces.Add(new Piece { Code = l.TovarCode, Name = l.TovarName, Price = l.Price, Marking = l.Marking });
                quantityOnDoc += l.Quantity; // дробный хвост тоже участвует в пороге (как на десктопе)
            }

            if (quantityOnDoc < h.Sum) return;

            _result.HaveAction = true;
            pieces.Sort((a, b) => a.Price.CompareTo(b.Price));

            int total = pieces.Count;
            int index = 0;
            var rebuilt = new List<CheckLine>();
            foreach (var p in pieces)
            {
                bool isGift = (index % groupSize == 0) && (index + groupSize <= total);
                var line = new CheckLine
                {
                    TovarCode = p.Code,
                    TovarName = p.Name,
                    Quantity = 1m,
                    Price = p.Price,
                    Marking = p.Marking
                };
                if (isGift)
                {
                    line.PriceAtDiscount = h.GiftPrice;
                    line.SumFull = p.Price;
                    line.SumAtDiscount = h.GiftPrice;
                    line.Action = 0;
                    line.Gift = h.NumDoc;
                }
                else
                {
                    line.PriceAtDiscount = p.Price;
                    line.SumFull = p.Price;
                    line.SumAtDiscount = p.Price;
                    line.Action = 0;
                    line.Gift = 0;
                }
                line.Action2 = h.NumDoc; // занимаем ВСЕ позиции акции
                rebuilt.Add(line);
                index++;
            }
            rebuilt.AddRange(untouched);
            _lines = rebuilt;

            MarkActionTovar(h);
        }

        // ════════════════════════════════════════════════════════════
        //  ТИП 5. Фиксированная сумма скидки на чек (kind=1, по акц. ШК).
        // ════════════════════════════════════════════════════════════
        private void Action5(ActionHeaderInfo h)
        {
            decimal sum = h.Sum;
            var codes = CodesOfDoc(h.NumDoc);
            foreach (var l in _lines)
            {
                if (l.Action2 > 0 || !IsPriceValidForAction(l)) continue;
                if (!codes.Contains(l.TovarCode)) continue;

                if (sum >= l.SumFull)
                {
                    // строка целиком гасится суммой скидки
                    sum -= l.SumFull;
                    // на десктопе из-за целочисленного деления (1/100) выходил 0 — здесь 0.01 по смыслу
                    l.PriceAtDiscount = MinPriceAfterDiscount;
                    l.SumFull = l.Quantity * l.Price;
                    l.SumAtDiscount = l.Quantity * MinPriceAfterDiscount;
                    l.Action = h.NumDoc;
                    l.Action2 = h.NumDoc;
                }
                else
                {
                    // остаток скидки меньше строки: сумма со скидкой = сумма − остаток скидки
                    l.SumAtDiscount = l.SumFull - sum;
                    l.PriceAtDiscount = Math.Round(l.SumAtDiscount / l.Quantity, 2, MidpointRounding.AwayFromZero);
                    l.Action = h.NumDoc;
                    l.Action2 = h.NumDoc;
                    break; // скидка исчерпана
                }
            }
            MarkActionTovar(h);
        }

        // ════════════════════════════════════════════════════════════
        //  ТИП 6. Стикеры/купоны: за каждые sum руб. по товарам списка — подарок.
        // ════════════════════════════════════════════════════════════
        private void Action6(ActionHeaderInfo h)
        {
            var eligible = EligibleLines(h, null);
            decimal actionSum = eligible.Sum(l => l.SumFull);
            if (h.Sum <= 0) return;

            int gifts = (int)(actionSum / h.Sum);
            if (gifts <= 0) return;

            _result.HaveAction = true;
            Msg(h.Comment + " количество подарков = " + gifts + " шт.");
            if (h.Marker == 1)
                Msg("Акция № " + h.NumDoc + ": требуется ввод штрихкодов подарков (" + gifts + " шт., marker=1).");
            MarkActionTovar(h);
        }

        // ════════════════════════════════════════════════════════════
        //  ТИП 8. Порог по количеству штук товаров списка.
        // ════════════════════════════════════════════════════════════

        /// <summary>persent≠0: скидка на все позиции списка при достижении порога количества.</summary>
        private void Action8Discount(ActionHeaderInfo h)
        {
            var eligible = EligibleLines(h, h.Persent);
            decimal quantityOnDoc = eligible.Sum(l => l.Quantity);
            if (quantityOnDoc < h.Sum) return;

            _result.HaveAction = true;
            foreach (var l in eligible)
            {
                ApplyPercentToLine(l, h.Persent, h.NumDoc);
                if (l.Price == l.PriceAtDiscount) l.Action = 0; // паритет: action только если цена изменилась
            }
            MarkActionTovar(h);
        }

        /// <summary>persent=0: сообщение «количество подарков = штук/sum».</summary>
        private void Action8Gift(ActionHeaderInfo h)
        {
            var eligible = EligibleLines(h, null);
            decimal quantityOnDoc = eligible.Sum(l => l.Quantity);
            if (quantityOnDoc < h.Sum) return;

            _result.HaveAction = true;
            int factor = (int)(quantityOnDoc / h.Sum);
            Msg(h.Comment.Trim() + " количество подарков = " + factor + " шт.");
            if (h.Marker == 1)
                Msg("Акция № " + h.NumDoc + ": требуется ввод штрихкодов подарков (" + factor + " шт., marker=1).");
            MarkActionTovar(h);
        }

        // ════════════════════════════════════════════════════════════
        //  ТИП 12. Сумма сп.2 → скидка на сп.1.
        //  fact2/sum1 = кратность; бюджет скидки = кратность × sum
        //  на товары списка 1; строка может раздваиваться.
        // ════════════════════════════════════════════════════════════
        private void Action12(ActionHeaderInfo h)
        {
            var byList = PositionsByList(h.NumDoc);
            int countList = byList.Count > 0 ? byList.Keys.Max() : 0;
            if (countList == 0) return;

            byList.TryGetValue(1, out var list1);
            byList.TryGetValue(2, out var list2);

            var eligible = _lines.Where(l => l.Action2 == 0 && IsPriceValidForAction(l, h.Persent)).ToList();

            decimal fact1 = eligible.Where(l => list1 != null && list1.Contains(l.TovarCode)).Sum(l => l.SumAtDiscount);
            // если 2-го списка нет — условным считается весь товарный состав (кроме списка 1)
            decimal fact2 = list2 != null
                ? eligible.Where(l => list2.Contains(l.TovarCode)).Sum(l => l.SumAtDiscount)
                : eligible.Where(l => list1 == null || !list1.Contains(l.TovarCode)).Sum(l => l.SumAtDiscount);

            if (h.Sum1 <= 0) return;
            int multiplicity = (int)(fact2 / h.Sum1);
            if (multiplicity <= 0 || fact1 <= 0) return;

            decimal budget = multiplicity * h.Sum;
            var list1Codes = eligible.Where(l => list1 != null && list1.Contains(l.TovarCode))
                                     .Select(l => l.TovarCode).Distinct().OrderBy(c => c).ToList();
            foreach (long code in list1Codes)
            {
                if (budget <= 0) break;
                foreach (var l in eligible.Where(x => x.TovarCode == code).ToList())
                {
                    if (budget <= 0) break;

                    if (budget >= l.SumAtDiscount)
                    {
                        // вся строка под скидку
                        budget -= l.SumAtDiscount;
                        l.PriceAtDiscount = Math.Round(l.Price - l.Price * h.Persent / 100m, 2, MidpointRounding.AwayFromZero);
                        l.SumAtDiscount = l.Quantity * l.PriceAtDiscount;
                        l.Action = h.NumDoc;
                        l.Action2 = h.NumDoc;
                    }
                    else
                    {
                        // скидка на часть количества: строка раздваивается
                        int requiredQty = (int)(budget / l.Price);
                        if (requiredQty <= 0) { budget = 0; break; } // десктоп создавал строку с 0 — здесь не создаём

                        var discounted = CloneLine(l);
                        discounted.Quantity = requiredQty;
                        discounted.PriceAtDiscount = Math.Round(l.Price - l.Price * h.Persent / 100m, 2, MidpointRounding.AwayFromZero);
                        discounted.SumFull = discounted.Quantity * l.Price;
                        discounted.SumAtDiscount = discounted.Quantity * discounted.PriceAtDiscount;
                        discounted.Action = h.NumDoc;
                        discounted.Action2 = h.NumDoc;
                        _lines.Add(discounted);

                        l.Quantity = l.Quantity - requiredQty;
                        l.SumFull = l.Quantity * l.Price;
                        l.SumAtDiscount = l.Quantity * l.PriceAtDiscount;
                        budget = 0;
                        break;
                    }
                }
            }

            // пометка участвовавших позиций
            if (countList == 2 && list2 != null)
            {
                foreach (var l in _lines)
                    if (l.Action2 == 0 && list2.Contains(l.TovarCode)) l.Action2 = h.NumDoc;
            }
            else if (countList == 1)
            {
                // 2-го списка нет: условный список — весь товарный состав чека
                foreach (var l in _lines)
                    if (l.Action2 != h.NumDoc) l.Action2 = h.NumDoc;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  ТИП 13. Градиентная (оптовая) цена по количеству.
        //  num_list в action_table — порог количества; берётся строка с
        //  максимальным num_list ≤ общего количества товара в чеке.
        //  Маркировка не трогается.
        // ════════════════════════════════════════════════════════════
        private void Action13(ActionHeaderInfo h)
        {
            if (!_positions.TryGetValue(h.NumDoc, out var positions) || positions.Count == 0) return;

            var targetCodes = _lines.Where(l => l.Action2 == 0).Select(l => l.TovarCode).Distinct().ToList();
            var actionPrices = new Dictionary<long, decimal>();
            foreach (long code in targetCodes)
            {
                decimal totalQty = _lines.Where(l => l.TovarCode == code && l.Action2 == 0).Sum(l => l.Quantity);
                var pos = positions.Where(p => p.CodeTovar == code && p.NumList <= totalQty)
                                   .OrderByDescending(p => p.NumList)
                                   .FirstOrDefault();
                if (pos != null) actionPrices[code] = pos.Price;
            }

            foreach (var l in _lines)
            {
                if (l.Action2 > 0) continue;
                if (!IsPriceValidForAction(l)) continue;
                if (!actionPrices.TryGetValue(l.TovarCode, out decimal actionPrice)) continue;

                l.PriceAtDiscount = actionPrice;
                l.SumFull = Math.Round(l.Quantity * l.Price, 2, MidpointRounding.AwayFromZero);
                l.SumAtDiscount = Math.Round(l.Quantity * actionPrice, 2, MidpointRounding.AwayFromZero);
                l.Action = h.NumDoc;
                l.Action2 = h.NumDoc;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  ТИП 10. Купонная кратность — цены не меняет.
        //  С табличной частью: порог = Σ sum_at_discount строк, входящих
        //  в акцию. Без неё: порог = action_header.sum от всего чека.
        //  Результат — сообщение + № акции в CouponActions (печать купонов).
        // ════════════════════════════════════════════════════════════
        private void CheckAction10()
        {
            decimal total = _lines.Sum(l => l.SumAtDiscount);
            foreach (var h in _activeHeaders.Where(x => x.Tip == 10))
            {
                bool hasPositions = _positions.TryGetValue(h.NumDoc, out var p) && p.Count > 0;
                if (hasPositions)
                {
                    if (h.BonusPromotion || h.Barcode.Trim().Length != 0) continue;
                    var codes = new HashSet<long>(p.Select(x => x.CodeTovar));
                    decimal divisor = _lines.Where(l => codes.Contains(l.TovarCode)).Sum(l => l.SumAtDiscount);
                    if (divisor > 0 && h.Sum <= divisor)
                    {
                        Msg("Кратность " + (int)(total / divisor) + " " + h.Comment);
                        AddCouponAction(h.NumDoc);
                    }
                }
                else
                {
                    if (h.Sum > 0 && h.Sum <= total)
                    {
                        Msg("Кратность " + (int)(total / h.Sum) + " " + h.Comment);
                        AddCouponAction(h.NumDoc);
                    }
                }
            }
        }

        // ════════════════════════════════════════════════════════════
        //  ЗАГРУЗКА АКЦИЙ ИЗ SQLITE
        // ════════════════════════════════════════════════════════════

        private void LoadActiveActions(DateTime today, int minutes)
        {
            _activeHeaders = new List<ActionHeaderInfo>();
            _positions.Clear();
            _codesOfDoc.Clear();

            var all = new List<ActionHeaderInfo>();
            using (var cmd = _db.CreateCommand())
            {
                // паритет с десктопом: its_deleted не фильтруется
                cmd.CommandText = @"SELECT num_doc, tip, kind, execution_order, marker, time_start, time_end,
                                           date_started, date_end, persent, ""sum"", sum1, gift_price,
                                           comment, barcode, bonus_promotion
                                    FROM action_header";
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) all.Add(ReadHeader(r));
            }

            foreach (var h in all)
            {
                bool dateOk = IsActiveByDate(h, today);
                if (!dateOk) continue;
                // паритет с десктопом: акции по штрихкоду (kind=1) временем действия не ограничиваются
                bool timeOk = h.Kind == 1 || (h.TimeStart <= minutes && minutes <= h.TimeEnd);
                if (!timeOk) continue;
                _activeHeaders.Add(h);
            }

            _activeHeaders = _activeHeaders.OrderBy(h => h.ExecutionOrder).ThenBy(h => h.Tip).ToList();
            if (_activeHeaders.Count == 0) return;

            // action_table одним пакетом, порциями по 500 num_doc (числа инлайнятся — они int)
            var nums = _activeHeaders.Select(h => h.NumDoc).Distinct().ToList();
            for (int i = 0; i < nums.Count; i += 500)
            {
                string inList = string.Join(",", nums.Skip(i).Take(500));
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "SELECT num_doc, code_tovar, num_list, price FROM action_table WHERE num_doc IN (" + inList + ")";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var pos = new ActionTablePosition
                            {
                                NumDoc = GetInt(r, 0),
                                CodeTovar = GetLong(r, 1),
                                NumList = GetInt(r, 2),
                                Price = GetDec(r, 3)
                            };
                            if (!_positions.TryGetValue(pos.NumDoc, out var list))
                                _positions[pos.NumDoc] = list = new List<ActionTablePosition>();
                            list.Add(pos);
                        }
                    }
                }
            }

            foreach (var h in _activeHeaders)
            {
                _codesOfDoc[h.NumDoc] = new HashSet<long>(
                    _positions.TryGetValue(h.NumDoc, out var p)
                        ? p.Select(x => x.CodeTovar)
                        : Enumerable.Empty<long>());
            }
        }

        private ActionHeaderInfo ReadHeader(SqliteDataReader r)
        {
            var h = new ActionHeaderInfo
            {
                NumDoc = GetInt(r, 0),
                Tip = GetInt(r, 1),
                Kind = GetInt(r, 2),
                ExecutionOrder = GetInt(r, 3),
                Marker = GetInt(r, 4),
                TimeStart = GetInt(r, 5),
                TimeEnd = GetInt(r, 6),
                DateStartedRaw = GetStr(r, 7),
                DateEndRaw = GetStr(r, 8),
                Persent = GetDec(r, 9),
                Sum = GetDec(r, 10),
                Sum1 = GetDec(r, 11),
                GiftPrice = GetDec(r, 12),
                Comment = GetStr(r, 13),
                Barcode = GetStr(r, 14),
                BonusPromotion = GetInt(r, 15) != 0
            };
            h.DateStarted = TryParseDate(h.DateStartedRaw);
            h.DateEnd = TryParseDate(h.DateEndRaw);
            return h;
        }

        /// <summary>
        /// Дата активна, если сегодня между date_started и date_end.
        /// Сначала пробуем распарсить (форматы 'yyyy-MM-dd' и 'dd.MM.yyyy'),
        /// при неудаче — строковое сравнение (как на десктопе).
        /// </summary>
        private bool IsActiveByDate(ActionHeaderInfo h, DateTime today)
        {
            if (h.DateStarted.HasValue && h.DateEnd.HasValue)
                return h.DateStarted.Value.Date <= today && today <= h.DateEnd.Value.Date;

            string t = today.ToString("yyyy-MM-dd");
            return string.CompareOrdinal(h.DateStartedRaw, t) <= 0
                && string.CompareOrdinal(t, h.DateEndRaw) <= 0;
        }

        private static DateTime? TryParseDate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();
            if (s.Length > 10) s = s.Substring(0, 10);
            if (DateTime.TryParseExact(s, new[] { "yyyy-MM-dd", "dd.MM.yyyy", "dd-MM-yyyy", "yyyy.MM.dd" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d))
                return d;
            return null;
        }

        private HashSet<int> LoadPersonalDocNums(string clientCode)
        {
            var res = new HashSet<int>();
            try
            {
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "SELECT DISTINCT num_doc FROM action_clients WHERE code_client = @c";
                    cmd.Parameters.AddWithValue("@c", clientCode);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) res.Add(GetInt(r, 0));
                }
            }
            catch
            {
                // таблицы action_clients нет — персональные акции недоступны
            }
            return res;
        }

        private bool PositionsIntersectCheck(int numDoc, HashSet<long> checkCodes)
        {
            return _codesOfDoc.TryGetValue(numDoc, out var codes) && codes.Overlaps(checkCodes);
        }

        /// <summary>
        /// День рождения клиента ±1 день (не 01.01.1900).
        /// ВНИМАНИЕ: таблицы clients нет в LoadPacketData — пока её не добавят
        /// в пакет с сервера, акция 9 типа не срабатывает (тихо возвращает false).
        /// </summary>
        private bool IsBirthdayClient()
        {
            if (_clientCode == "") return false;
            try
            {
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "SELECT date_of_birth FROM clients WHERE code = @c LIMIT 1";
                    cmd.Parameters.AddWithValue("@c", _clientCode);
                    var raw = cmd.ExecuteScalar()?.ToString();
                    if (string.IsNullOrWhiteSpace(raw)) return false;

                    string s = raw.Trim();
                    if (s.Length > 10) s = s.Substring(0, 10);
                    if (!DateTime.TryParseExact(s, new[] { "dd.MM.yyyy", "yyyy-MM-dd", "dd-MM-yyyy" },
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d)) return false;
                    if (d.Year == 1900 && d.Month == 1 && d.Day == 1) return false;

                    DateTime now = DateTime.Now.Date;
                    return d.Month == now.Month
                        && d.Day >= now.AddDays(-1).Day
                        && d.Day <= now.AddDays(1).Day;
                }
            }
            catch
            {
                return false; // таблицы clients нет — акция 9 не срабатывает
            }
        }

        // ════════════════════════════════════════════════════════════
        //  ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
        // ════════════════════════════════════════════════════════════

        /// <summary>Пометка позиций, участвовавших в акции (action2), чтобы не участвовали в следующих.</summary>
        private void MarkActionTovar(ActionHeaderInfo h)
        {
            var codes = CodesOfDoc(h.NumDoc);
            foreach (var l in _lines)
            {
                if (l.Action2 > 0) continue;
                if (!IsPriceValidForAction(l)) continue;
                if (codes.Contains(l.TovarCode))
                {
                    l.Action2 = h.NumDoc;
                    l.PromoDescription = h.Comment;
                }
            }
        }

        /// <summary>Строки, которые могут участвовать в акции: свободны, из списка, цена подходит.</summary>
        private List<CheckLine> EligibleLines(ActionHeaderInfo h, decimal? percent)
        {
            var codes = CodesOfDoc(h.NumDoc);
            return _lines.Where(l => l.Action2 == 0
                                  && codes.Contains(l.TovarCode)
                                  && (percent.HasValue ? IsPriceValidForAction(l, percent.Value)
                                                       : IsPriceValidForAction(l)))
                         .ToList();
        }

        /// <summary>Базовая проверка для акций без процента (подарки, сообщения): цена со скидкой ≥ 1.</summary>
        private bool IsPriceValidForAction(CheckLine l)
        {
            return l.PriceAtDiscount >= 1m;
        }

        /// <summary>Расширенная проверка для акций с процентом: цена после скидки не обнулится.</summary>
        private bool IsPriceValidForAction(CheckLine l, decimal percent)
        {
            decimal remaining = (100m - percent) / 100m;
            if (remaining <= 0m) return false; // скидка 100% и больше
            return l.Price * remaining >= MinPriceAfterDiscount;
        }

        /// <summary>Процентная скидка на строку (аналог ApplyDiscountToRow десктопа).</summary>
        private void ApplyPercentToLine(CheckLine l, decimal percent, int numDoc)
        {
            decimal priceAtDiscount = Math.Round(l.Price - l.Price * percent / 100m, 2, MidpointRounding.AwayFromZero);
            l.PriceAtDiscount = priceAtDiscount;
            l.SumFull = Math.Round(l.Quantity * l.Price, 2, MidpointRounding.AwayFromZero);
            l.SumAtDiscount = Math.Round(l.Quantity * priceAtDiscount, 2, MidpointRounding.AwayFromZero);
            l.Action = numDoc;
            l.Action2 = numDoc;
        }

        /// <summary>Склейка одинаковых строк после раздвоений (аналог roll_up_dt без temp-таблицы).</summary>
        private List<CheckLine> RollUp(List<CheckLine> source)
        {
            var result = new List<CheckLine>();
            var index = new Dictionary<string, CheckLine>();
            foreach (var l in source)
            {
                string key = string.Join("|",
                    l.TovarCode.ToString(CultureInfo.InvariantCulture),
                    l.Price.ToString(CultureInfo.InvariantCulture),
                    l.PriceAtDiscount.ToString(CultureInfo.InvariantCulture),
                    l.Action, l.Gift, l.Action2, l.Marking);

                if (index.TryGetValue(key, out var g))
                {
                    g.Quantity += l.Quantity;
                    g.SumFull += l.SumFull;
                    g.SumAtDiscount += l.SumAtDiscount;
                    if (string.IsNullOrEmpty(g.PromoDescription)) g.PromoDescription = l.PromoDescription;
                }
                else
                {
                    var clone = CloneLine(l);
                    index[key] = clone;
                    result.Add(clone);
                }
            }
            return result;
        }

        private Dictionary<long, decimal> PositionPrices(int numDoc)
        {
            var res = new Dictionary<long, decimal>();
            if (_positions.TryGetValue(numDoc, out var list))
                foreach (var p in list)
                    res[p.CodeTovar] = p.Price; // при дублях побеждает последняя — паритет с десктопом
            return res;
        }

        private Dictionary<int, HashSet<long>> PositionsByList(int numDoc)
        {
            var res = new Dictionary<int, HashSet<long>>();
            if (_positions.TryGetValue(numDoc, out var list))
                foreach (var p in list)
                {
                    if (!res.TryGetValue(p.NumList, out var set))
                        res[p.NumList] = set = new HashSet<long>();
                    set.Add(p.CodeTovar);
                }
            return res;
        }

        private HashSet<long> CodesOfDoc(int numDoc)
        {
            return _codesOfDoc.TryGetValue(numDoc, out var codes) ? codes : new HashSet<long>();
        }

        private void Msg(string text)
        {
            if (!string.IsNullOrWhiteSpace(text)) _result.Messages.Add(text);
        }

        private void AddCouponAction(int numDoc)
        {
            if (!_result.CouponActions.Contains(numDoc)) _result.CouponActions.Add(numDoc);
        }

        private static CheckLine CloneLine(CheckLine l)
        {
            return new CheckLine
            {
                TovarCode = l.TovarCode,
                TovarName = l.TovarName,
                Quantity = l.Quantity,
                Price = l.Price,
                PriceAtDiscount = l.PriceAtDiscount,
                SumFull = l.SumFull,
                SumAtDiscount = l.SumAtDiscount,
                Action = l.Action,
                Gift = l.Gift,
                Action2 = l.Action2,
                Marking = l.Marking,
                PromoDescription = l.PromoDescription
            };
        }

        // ★ Толерантные хелперы: колонки time_start/time_end в SQLite имеют тип TEXT.
        //   Пустая строка "" или нечисловое значение бросили бы FormatException в Convert.ToInt32 —
        //   поэтому строки разбираем через TryParse (не распарсилось → 0).
        private static int GetInt(SqliteDataReader r, int i)
        {
            if (r.IsDBNull(i)) return 0;
            object v = r.GetValue(i);
            if (v is string s)
                return int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out int n) ? n : 0;
            return Convert.ToInt32(v, CultureInfo.InvariantCulture);
        }

        private static long GetLong(SqliteDataReader r, int i)
        {
            if (r.IsDBNull(i)) return 0L;
            object v = r.GetValue(i);
            if (v is string s)
                return long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out long n) ? n : 0L;
            return Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }

        private static decimal GetDec(SqliteDataReader r, int i)
        {
            if (r.IsDBNull(i)) return 0m;
            object v = r.GetValue(i);
            if (v is string s)
                return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal n) ? n : 0m;
            return Convert.ToDecimal(v, CultureInfo.InvariantCulture);
        }
        private static string GetStr(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : (r.GetValue(i)?.ToString() ?? "");

        // ── внутренние структуры для «развёртки» штук в акциях 4 типа ──
        private class Piece
        {
            public long Code;
            public string Name;
            public decimal Price;
            public string Marking;
        }

        private class ListGroup
        {
            public long Code;
            public string Name;
            public decimal Price;
            public decimal FinalPrice;
            public decimal Count;
            public decimal SumFull;
            public decimal SumDiscount;
            public int Action;
            public bool IsMarked;
            public string Marking;
        }
    }
}
