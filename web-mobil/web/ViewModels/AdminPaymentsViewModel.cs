namespace NSYazilim.Web.ViewModels
{
    public class AdminPaymentsViewModel
    {
        public List<AdminPaymentRowViewModel> Rows { get; set; } = new();
        public string Query { get; set; } = string.Empty;
        public string PaymentMethod { get; set; } = string.Empty;
        public int TotalCount { get; set; }
        public int CardCount { get; set; }
        public int BankTransferCount { get; set; }
        public int ManualSaleCount { get; set; }
        public int PendingBankTransferCount { get; set; }
        public decimal PaidTotal { get; set; }
        public decimal CardPaidTotal { get; set; }
        public decimal BankTransferPaidTotal { get; set; }
        public decimal ManualSalePaidTotal { get; set; }
        public bool ShopierConfigured { get; set; }
        public bool ShopierWebhookConfigured { get; set; }
        public string? ShopierStatusMessage { get; set; }
    }

    public class AdminPaymentRowViewModel
    {
        public string Reference { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public string CustomerEmail { get; set; } = string.Empty;
        public string CustomerPhone { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string LicenseLabel { get; set; } = string.Empty;
        public string PaymentType { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public string ProviderDetail { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "TRY";
        public string Status { get; set; } = string.Empty;
        public string StatusCss { get; set; } = "neutral";
        public string StatusDetail { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public bool IsPaid { get; set; }
        public int? LocalOrderId { get; set; }
        public int? BankTransferId { get; set; }
        public string SourceType { get; set; } = string.Empty;
        public int SourceId { get; set; }
    }
}
