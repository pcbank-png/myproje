namespace NSYazilim.Web.Services
{
    /// <summary>
    /// Checkout is a revenue-critical surface. The main localization engine remains the
    /// authority, but these reviewed fallbacks prevent Turkish leakage while a newly added
    /// language/phrase is still waiting in the machine-translation queue.
    /// </summary>
    public static class CheckoutUiTranslations
    {
        private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Items =
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["Ödeme Yöntemi | NSX Yazılım"] = M("Payment Method | NSX Software", "Способ оплаты | NSX Software", "Zahlungsart | NSX Software", "Método de pago | NSX Software", "طريقة الدفع | NSX Software", "Mode de paiement | NSX Software"),
                ["Ürüne dön"] = M("Back to product", "Вернуться к товару", "Zurück zum Produkt", "Volver al producto", "العودة إلى المنتج", "Retour au produit"),
                ["GÜVENLİ ÖDEME"] = M("SECURE PAYMENT", "БЕЗОПАСНАЯ ОПЛАТА", "SICHERE ZAHLUNG", "PAGO SEGURO", "دفع آمن", "PAIEMENT SÉCURISÉ"),
                ["Ödeme yöntemini seçin"] = M("Choose a payment method", "Выберите способ оплаты", "Zahlungsart wählen", "Elige un método de pago", "اختر طريقة الدفع", "Choisissez un mode de paiement"),
                ["Kartla Shopier üzerinden anında ödeme yapabilir veya Havale / EFT ile devam edebilirsiniz."] = M("Pay instantly by card through Shopier or continue with bank transfer / EFT.", "Оплатите картой через Shopier мгновенно или продолжите банковским переводом / EFT.", "Zahlen Sie sofort per Karte über Shopier oder fahren Sie mit Banküberweisung / EFT fort.", "Paga al instante con tarjeta mediante Shopier o continúa con transferencia bancaria / EFT.", "ادفع فورًا بالبطاقة عبر Shopier أو تابع عبر التحويل البنكي / EFT.", "Payez instantanément par carte via Shopier ou continuez par virement bancaire / EFT."),
                ["Ödemeleriniz 256-bit SSL ile koruma altındadır."] = M("Your payments are protected with 256-bit SSL.", "Ваши платежи защищены 256-битным SSL.", "Ihre Zahlungen sind durch 256-Bit-SSL geschützt.", "Tus pagos están protegidos con SSL de 256 bits.", "مدفوعاتك محمية بتشفير SSL ‏256 بت.", "Vos paiements sont protégés par SSL 256 bits."),
                ["Seçili ürün"] = M("Selected product", "Выбранный товар", "Ausgewähltes Produkt", "Producto seleccionado", "المنتج المحدد", "Produit sélectionné"),
                ["Yıllık Lisans"] = M("Annual License", "Годовая лицензия", "Jahreslizenz", "Licencia anual", "ترخيص سنوي", "Licence annuelle"),
                ["Sınırsız Lisans"] = M("Lifetime License", "Бессрочная лицензия", "Unbefristete Lizenz", "Licencia de por vida", "ترخيص مدى الحياة", "Licence à vie"),
                ["Tutar"] = M("Amount", "Сумма", "Betrag", "Importe", "المبلغ", "Montant"),
                ["ÖNERİLEN"] = M("RECOMMENDED", "РЕКОМЕНДУЕТСЯ", "EMPFOHLEN", "RECOMENDADO", "موصى به", "RECOMMANDÉ"),
                ["KARTLA ÖDEME"] = M("CARD PAYMENT", "ОПЛАТА КАРТОЙ", "KARTENZAHLUNG", "PAGO CON TARJETA", "الدفع بالبطاقة", "PAIEMENT PAR CARTE"),
                ["Banka / Kredi Kartı"] = M("Debit / Credit Card", "Банковская / кредитная карта", "Bank-/Kreditkarte", "Tarjeta bancaria / crédito", "بطاقة مصرفية / ائتمانية", "Carte bancaire / crédit"),
                ["Shopier güvenli ödeme altyapısı üzerinden kartınızla hızlı ve güvenli ödeme yapın."] = M("Pay quickly and securely by card through Shopier's secure payment infrastructure.", "Оплачивайте картой быстро и безопасно через защищенную платежную систему Shopier.", "Zahlen Sie schnell und sicher per Karte über die sichere Shopier-Zahlungsinfrastruktur.", "Paga con tarjeta de forma rápida y segura mediante la infraestructura de pago segura de Shopier.", "ادفع بالبطاقة بسرعة وأمان عبر بنية الدفع الآمنة من Shopier.", "Payez rapidement et en toute sécurité par carte via l'infrastructure de paiement sécurisée de Shopier."),
                ["Anında ödeme"] = M("Instant payment", "Мгновенная оплата", "Sofortige Zahlung", "Pago instantáneo", "دفع فوري", "Paiement instantané"),
                ["256-bit SSL güvenliği"] = M("256-bit SSL security", "Защита SSL 256 бит", "256-Bit-SSL-Sicherheit", "Seguridad SSL de 256 bits", "أمان SSL ‏256 بت", "Sécurité SSL 256 bits"),
                ["Tüm yaygın kartlarla ödeme"] = M("All major cards accepted", "Принимаются основные карты", "Alle gängigen Karten", "Aceptamos las principales tarjetas", "قبول البطاقات الرئيسية", "Principales cartes acceptées"),
                ["Shopier ile Güvenli Öde"] = M("Pay Securely with Shopier", "Безопасно оплатить через Shopier", "Sicher mit Shopier bezahlen", "Pagar de forma segura con Shopier", "ادفع بأمان عبر Shopier", "Payer en toute sécurité avec Shopier"),
                ["Shopier ekranında lisans seçiminizi doğrulayın."] = M("Confirm your license selection on the Shopier screen.", "Проверьте выбранную лицензию на странице Shopier.", "Bestätigen Sie Ihre Lizenzauswahl auf der Shopier-Seite.", "Confirma tu selección de licencia en la pantalla de Shopier.", "تحقق من اختيار الترخيص في صفحة Shopier.", "Confirmez votre choix de licence sur l'écran Shopier."),
                ["Otomatik lisans teslimatı için Shopier'de hesabınızdaki e-posta adresini kullanın:"] = M("For automatic license delivery, use your account email on Shopier:", "Для автоматической доставки лицензии используйте в Shopier адрес электронной почты вашей учетной записи:", "Verwenden Sie für die automatische Lizenzzustellung bei Shopier die E-Mail-Adresse Ihres Kontos:", "Para la entrega automática de la licencia, usa en Shopier el correo electrónico de tu cuenta:", "للتسليم التلقائي للترخيص، استخدم بريد حسابك الإلكتروني في Shopier:", "Pour la livraison automatique de la licence, utilisez sur Shopier l'e-mail de votre compte :"),
                ["Bu ürün için kartla ödeme bağlantısı henüz tanımlı değil."] = M("The card-payment link for this product is not ready yet.", "Ссылка для оплаты картой этого товара пока не готова.", "Der Karten-Zahlungslink für dieses Produkt ist noch nicht verfügbar.", "El enlace de pago con tarjeta para este producto aún no está listo.", "رابط الدفع بالبطاقة لهذا المنتج غير جاهز بعد.", "Le lien de paiement par carte pour ce produit n'est pas encore prêt."),
                ["Shopier eşleştirmesi bekleniyor"] = M("Waiting for Shopier matching", "Ожидание сопоставления с Shopier", "Shopier-Zuordnung ausstehend", "Esperando vinculación con Shopier", "بانتظار مطابقة Shopier", "En attente de l'association Shopier"),
                ["BANKA TRANSFERİ"] = M("BANK TRANSFER", "БАНКОВСКИЙ ПЕРЕВОД", "BANKÜBERWEISUNG", "TRANSFERENCIA BANCARIA", "تحويل بنكي", "VIREMENT BANCAIRE"),
                ["Havale / EFT"] = M("Bank Transfer / EFT", "Банковский перевод / EFT", "Banküberweisung / EFT", "Transferencia bancaria / EFT", "تحويل بنكي / EFT", "Virement bancaire / EFT"),
                ["Banka hesabımıza havale yaptıktan sonra ödeme bildiriminizi gönderin. Onay sonrası lisansınız hesabınıza tanımlanır."] = M("Send your payment notification after the bank transfer. Your license is added to your account after approval.", "После банковского перевода отправьте уведомление об оплате. После подтверждения лицензия будет добавлена в ваш аккаунт.", "Senden Sie nach der Überweisung Ihre Zahlungsbestätigung. Nach der Freigabe wird die Lizenz Ihrem Konto hinzugefügt.", "Envía la notificación de pago después de la transferencia. Tras la aprobación, la licencia se añadirá a tu cuenta.", "أرسل إشعار الدفع بعد التحويل البنكي. بعد الموافقة ستتم إضافة الترخيص إلى حسابك.", "Envoyez votre notification de paiement après le virement. Après validation, la licence sera ajoutée à votre compte."),
                ["Tüm bankalardan ödeme"] = M("Payments from all banks", "Перевод из любого банка", "Zahlung von allen Banken", "Pagos desde cualquier banco", "الدفع من جميع البنوك", "Paiement depuis toutes les banques"),
                ["Güvenli ve pratik"] = M("Secure and convenient", "Безопасно и удобно", "Sicher und praktisch", "Seguro y práctico", "آمن وسهل", "Sûr et pratique"),
                ["Onay sonrası hızlı işlem"] = M("Fast processing after approval", "Быстрая обработка после подтверждения", "Schnelle Bearbeitung nach Freigabe", "Procesamiento rápido tras la aprobación", "معالجة سريعة بعد الموافقة", "Traitement rapide après validation"),
                ["Banka bilgilerimizi görüntüleyin"] = M("View our bank details", "Посмотреть банковские реквизиты", "Bankdaten anzeigen", "Ver nuestros datos bancarios", "عرض بياناتنا البنكية", "Afficher nos coordonnées bancaires"),
                ["Havale / EFT için gerekli hesap bilgileri, açıklama ve ödeme sonrası adımlar."] = M("Account details, transfer note and post-payment steps for bank transfer / EFT.", "Реквизиты счета, назначение перевода и дальнейшие шаги после оплаты.", "Kontodaten, Verwendungszweck und Schritte nach der Zahlung für Banküberweisung / EFT.", "Datos de cuenta, concepto y pasos posteriores al pago para transferencia / EFT.", "بيانات الحساب ووصف التحويل وخطوات ما بعد الدفع للتحويل البنكي / EFT.", "Coordonnées bancaires, libellé et étapes après paiement pour virement / EFT."),
                ["Banka Bilgilerini Gör"] = M("View Bank Details", "Показать банковские реквизиты", "Bankdaten anzeigen", "Ver datos bancarios", "عرض البيانات البنكية", "Voir les coordonnées bancaires"),
                ["Güvenli Ödeme Altyapısı"] = M("Secure Payment Infrastructure", "Безопасная платежная система", "Sichere Zahlungsinfrastruktur", "Infraestructura de pago segura", "بنية دفع آمنة", "Infrastructure de paiement sécurisée"),
                ["Kart ödemeleri Shopier güvenli ödeme altyapısı üzerinden tamamlanır."] = M("Card payments are completed through Shopier's secure payment infrastructure.", "Платежи картой проходят через защищенную платежную систему Shopier.", "Kartenzahlungen werden über die sichere Shopier-Zahlungsinfrastruktur abgewickelt.", "Los pagos con tarjeta se completan mediante la infraestructura segura de Shopier.", "تتم مدفوعات البطاقات عبر بنية الدفع الآمنة من Shopier.", "Les paiements par carte sont traités via l'infrastructure sécurisée de Shopier."),
                ["Havale / EFT ile Ödeme"] = M("Pay by Bank Transfer / EFT", "Оплата банковским переводом / EFT", "Zahlung per Banküberweisung / EFT", "Pago por transferencia bancaria / EFT", "الدفع عبر التحويل البنكي / EFT", "Paiement par virement bancaire / EFT"),
                ["Ödeme onayından sonra dijital lisansınız hesabınıza tanımlanır."] = M("Your digital license is added to your account after payment approval.", "После подтверждения оплаты цифровая лицензия будет добавлена в ваш аккаунт.", "Nach Zahlungsbestätigung wird Ihre digitale Lizenz Ihrem Konto hinzugefügt.", "Tras aprobarse el pago, la licencia digital se añadirá a tu cuenta.", "بعد الموافقة على الدفع، تتم إضافة الترخيص الرقمي إلى حسابك.", "Après validation du paiement, votre licence numérique est ajoutée à votre compte."),
                ["Dijital teslimat"] = M("Digital delivery", "Цифровая доставка", "Digitale Lieferung", "Entrega digital", "تسليم رقمي", "Livraison numérique"),
                ["Güvenli lisanslama"] = M("Secure licensing", "Безопасное лицензирование", "Sichere Lizenzierung", "Licenciamiento seguro", "ترخيص آمن", "Licence sécurisée"),
                ["NSX teknik destek"] = M("NSX technical support", "Техническая поддержка NSX", "NSX-Techniksupport", "Soporte técnico NSX", "الدعم الفني من NSX", "Support technique NSX"),
                ["Güvenli kart ödemesi"] = M("Secure card payment", "Безопасная оплата картой", "Sichere Kartenzahlung", "Pago seguro con tarjeta", "دفع آمن بالبطاقة", "Paiement sécurisé par carte"),
                ["Shopier altyapısı"] = M("Shopier infrastructure", "Инфраструктура Shopier", "Shopier-Infrastruktur", "Infraestructura Shopier", "بنية Shopier", "Infrastructure Shopier")
            };

        public static string Resolve(string source, string? languageCode, string? preferred = null)
        {
            source ??= string.Empty;
            var neutral = NormalizeLanguage(languageCode);
            var candidate = (preferred ?? string.Empty).Trim();

            if (neutral == "tr")
                return string.IsNullOrWhiteSpace(candidate) ? source : candidate;

            if (!Items.TryGetValue(source, out var translations))
                return string.IsNullOrWhiteSpace(candidate) ? source : candidate;

            translations.TryGetValue("en", out var builtInEnglish);

            // A real target-language DB translation always wins. SiteLocalization can return
            // reviewed English as a safe fallback; in that case prefer our target fallback.
            if (!string.IsNullOrWhiteSpace(candidate)
                && !string.Equals(candidate, source, StringComparison.Ordinal)
                && (neutral == "en" || string.IsNullOrWhiteSpace(builtInEnglish)
                    || !string.Equals(candidate, builtInEnglish, StringComparison.Ordinal)))
            {
                return candidate;
            }

            if (translations.TryGetValue(neutral, out var target) && !string.IsNullOrWhiteSpace(target))
                return target;

            if (!string.IsNullOrWhiteSpace(candidate) && !string.Equals(candidate, source, StringComparison.Ordinal))
                return candidate;

            return !string.IsNullOrWhiteSpace(builtInEnglish) ? builtInEnglish : source;
        }

        private static string NormalizeLanguage(string? languageCode)
        {
            var code = (languageCode ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(code))
                return "tr";
            var separator = code.IndexOfAny(new[] { '-', '_' });
            return separator > 0 ? code[..separator] : code;
        }

        private static IReadOnlyDictionary<string, string> M(
            string en, string ru, string de, string es, string ar, string fr)
            => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = en,
                ["ru"] = ru,
                ["de"] = de,
                ["es"] = es,
                ["ar"] = ar,
                ["fr"] = fr
            };
    }
}
