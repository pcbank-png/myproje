namespace NSYazilim.Web.Services
{
    public static class StatusTextHelper
    {
        public static string OrderStatus(string? status)
        {
            return Normalize(status) switch
            {
                "new" => "Yeni Sipariş",
                "waitingpayment" => "Ödeme Bekleniyor",
                "waitingbanktransfer" => "Havale Bekleniyor",
                "banktransferrejected" => "Havale Reddedildi",
                "paymentreceivedstockproblem" => "Ödeme Alındı, Stok Kontrolü Gerekli",
                "paymentfailed" => "Ödeme Başarısız",
                "completed" => "Tamamlandı",
                "delivered" => "Teslim Edildi",
                "paid" => "Ödendi",
                "cancelled" => "İptal Edildi",
                "canceled" => "İptal Edildi",
                "refunded" => "İade Edildi",
                _ => Fallback(status)
            };
        }

        public static string PaymentStatus(string? status)
        {
            return Normalize(status) switch
            {
                "pending" => "Beklemede",
                "banktransferpending" => "Havale Bekleniyor",
                "paid" => "Ödendi",
                "completed" => "Tamamlandı",
                "failed" => "Başarısız",
                "paymentfailed" => "Ödeme Başarısız",
                "stockproblem" => "Stok Kontrolü Gerekli",
                "rejected" => "Reddedildi",
                "cancelled" => "İptal Edildi",
                "canceled" => "İptal Edildi",
                "refunded" => "İade Edildi",
                _ => OrderStatus(status)
            };
        }

        public static string BankTransferStatus(string? status)
        {
            return Normalize(status) switch
            {
                "pending" => "Onay Bekliyor",
                "approved" => "Onaylandı",
                "rejected" => "Reddedildi",
                "stockproblem" => "Stok Kontrolü Gerekli",
                _ => Fallback(status)
            };
        }

        private static string Normalize(string? status)
        {
            return (status ?? string.Empty).Trim().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string Fallback(string? status)
        {
            return string.IsNullOrWhiteSpace(status) ? "Belirtilmedi" : status.Trim();
        }
    }
}
