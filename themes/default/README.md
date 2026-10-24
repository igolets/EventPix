# Default theme

Files: `index.html` (shell), `templates.html` (markup for each view), `theme.css` (all visuals).
To make your own: copy this folder, rename it, set `EventPix:ActiveTheme`. Keep every `data-tpl` and `data-ref`
name from `templates.html`; everything else (structure, classes, fonts, images) is yours.
Add fonts/images as files in the theme folder and reference them as `/theme/<file>`.
Restrictions (strict CSP): no inline scripts, no inline `style=""`, no remote hosts.
