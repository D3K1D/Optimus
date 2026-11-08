// ?theme=light picks the Light theme before first paint (moved out of an inline script for the CSP).
if (/[?&]theme=light/.test(location.search)) document.documentElement.setAttribute('data-theme', 'light');
