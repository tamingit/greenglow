// The demo's start page (demo.html): one button that signs the visitor in as a guest
// (POST /auth/guest) and goes back to where they were sent from.

import '../style.css';
import './demo.css';

const returnUrl = new URLSearchParams(location.search).get('returnUrl');
const field = document.querySelector<HTMLInputElement>('#demo-return');
// The server takes only a path on this site, whatever is sent.
if (field && returnUrl?.startsWith('/')) field.value = returnUrl;
