using System.Collections.Generic;
using Newtonsoft.Json;

namespace CashPlace
{
    public class SalesPortions
    {
        public string Version { get; set; } = "";
        public string Shop { get; set; } = "";
        public string Guid { get; set; } = "";
        public List<SalesPortionsHeader> ListSalesPortionsHeader { get; set; }
        public List<SalesPortionsTable> ListSalesPortionsTable { get; set; }
    }

    public class SalesPortionsHeader
    {
        // Оставлены ТОЛЬКО те поля, которые есть в таблице sales_header на SQL сервере
        public string Shop { get; set; } = "";
        public string Num_doc { get; set; } = "";
        public string Num_cash { get; set; } = "11";
        public string Client { get; set; } = "";          // -> card_id
        public string Card_number { get; set; } = "";     // -> card_number
        public string Discount { get; set; } = "0";
        public string Sum { get; set; } = "0";
        public string Check_type { get; set; } = "0";
        public string Have_action { get; set; } = "0";
        public string Its_deleted { get; set; } = "0";
        public string Bonus_counted { get; set; } = "0";
        public string Bonus_writen_off { get; set; } = "0";
        public string Date_time_write { get; set; } = "";
        public string Date_time_start { get; set; } = "";
        public string Sum_cash { get; set; } = "0";
        public string Sum_terminal { get; set; } = "0";
        public string Sum_certificate { get; set; } = "0";
        public string Sum_cash1 { get; set; } = "0";
        public string Sum_terminal1 { get; set; } = "0";
        public string Sum_certificate1 { get; set; } = "0";
        public string SumCashRemainder { get; set; } = "0";
        public string Comment { get; set; } = "";
        public string ClientInfo_vatin { get; set; } = "";
        public string ClientInfo_name { get; set; } = "";
        public string Autor { get; set; } = "910214609785";
        public string Version { get; set; } = "0";
        public string Its_print { get; set; } = "0";
        public string Id_transaction { get; set; } = "0";      // -> transactionId
        public string Id_transaction_sale { get; set; } = "0"; // -> transactionIdSales
        public string NumOrder { get; set; } = "";             // -> sales_receipt
        public string SystemTaxation { get; set; } = "0";      // -> sno
        public string Guid { get; set; } = "";
        public string SBP { get; set; } = "0";
        public string Extra { get; set; } = "0";
    }

    public class SalesPortionsTable
    {
        // Оставлены ТОЛЬКО те поля, которые есть в таблице sales_table на SQL сервере
        // Поле num_cash ПОЛНОСТЬЮ УДАЛЕНО, так как его нет в таблице sales_table
        public string Shop { get; set; } = "";
        public string Num_doc { get; set; } = "";
        public string Num_cash { get; set; } = "11";
        public string Tovar { get; set; } = "0";
        public string Quantity { get; set; } = "0";
        public string Price { get; set; } = "0";
        public string Price_d { get; set; } = "0";
        public string Sum { get; set; } = "0";
        public string Sum_d { get; set; } = "0";
        public string Action1 { get; set; } = "0";
        public string Action2 { get; set; } = "0";
        public string Action3 { get; set; } = "0";
        public string Date_time_write { get; set; } = "";
        public string Num_str { get; set; } = "0";
        public string Bonus_stand { get; set; } = "0";
        public string Bonus_prom { get; set; } = "0";
        public string Promotion_b_mover { get; set; } = "0";
        public string MarkingCode { get; set; } = "";
        public string Guid { get; set; } = "";
    }
}