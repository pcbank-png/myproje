const nsxAccountEnglish = (document.documentElement.lang || '').toLowerCase().startsWith('en');
const nsxAccountUiLocalization = document.querySelector('[data-account-ui-localization]');
const nsxAccountUiText = (selector, fallback) => {
  const value = nsxAccountUiLocalization?.querySelector(selector)?.textContent?.trim();
  return value || fallback;
};

document.querySelectorAll('[data-password-toggle]').forEach(button => {
  const showText = button.textContent?.trim() || (nsxAccountEnglish ? 'Show' : 'Göster');
  const showAria = button.getAttribute('aria-label') || (nsxAccountEnglish ? 'Show password' : 'Şifreyi göster');
  const hideText = nsxAccountUiText('[data-password-hide-label]', nsxAccountEnglish ? 'Hide' : 'Gizle');
  const hideAria = nsxAccountUiText('[data-password-hide-aria]', nsxAccountEnglish ? 'Hide password' : 'Şifreyi gizle');

  button.addEventListener('click', () => {
    const field = button.closest('.nsx-password-field')?.querySelector('input');
    if (!field) return;
    const show = field.type === 'password';
    field.type = show ? 'text' : 'password';
    button.textContent = show ? hideText : showText;
    button.setAttribute('aria-label', show ? hideAria : showAria);
  });
});

document.querySelectorAll('[data-copy-target]').forEach(button => {
  button.addEventListener('click', async () => {
    const target = document.getElementById(button.dataset.copyTarget || '');
    if (!target) return;
    try {
      await navigator.clipboard.writeText(target.textContent?.trim() || '');
      const original = button.textContent;
      button.textContent = nsxAccountEnglish ? 'Copied' : 'Kopyalandı';
      window.setTimeout(() => button.textContent = original, 1600);
    } catch {
      window.getSelection()?.selectAllChildren(target);
    }
  });
});
