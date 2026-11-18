import js from '@eslint/js';
import { defineConfig } from 'eslint/config';
import globals from 'globals';
import tseslint from 'typescript-eslint';

/** An address from the root of the server, which misses the app's base path. */
const APP_PATH =
  '/^\\/(api|ws|auth|branding|icons|help|brand|manifest\\.webmanifest|[a-z]+\\.html)([/?#]|$)/';
const APP_PATH_MESSAGE =
  "Make the address relative to the page ('api/me', not '/api/me'): the app may be under a base path. See src/urls.ts.";

export default defineConfig(
  { ignores: ['**/dist/', '**/node_modules/', 'test/recordings/', 'test-results/'] },
  js.configs.recommended,
  tseslint.configs.recommendedTypeChecked,
  {
    languageOptions: {
      parserOptions: {
        projectService: {
          allowDefaultProject: ['*.js', '*.ts', 'packages/web/vite.config.ts'],
          defaultProject: 'tsconfig.base.json',
        },
        tsconfigRootDir: import.meta.dirname,
      },
    },
  },
  {
    files: ['**/*.js'],
    extends: [tseslint.configs.disableTypeChecked],
    languageOptions: { globals: globals.node },
  },
  {
    // The server serves the app with a CSP of default-src 'self', which blocks style
    // attributes and inline markup. Styles go through CSS custom properties
    // (style.setProperty), so every look stays in its stylesheet.
    files: ['packages/web/src/**/*.ts'],
    rules: {
      'no-restricted-syntax': [
        'error',
        {
          selector:
            'AssignmentExpression > MemberExpression.left[property.name=/^(innerHTML|outerHTML)$/]',
          message: 'Build DOM nodes instead of HTML strings (CSP, and no markup from the host).',
        },
        {
          selector: "CallExpression[callee.property.name='insertAdjacentHTML']",
          message: 'Build DOM nodes instead of HTML strings (CSP, and no markup from the host).',
        },
        {
          selector:
            "CallExpression[callee.property.name='setAttribute'][arguments.0.value='style']",
          message: 'The CSP blocks style attributes. Set a custom property with style.setProperty.',
        },
        {
          selector: "AssignmentExpression > MemberExpression.left[object.property.name='style']",
          message: 'Set a custom property with style.setProperty, so the look stays in CSS.',
        },
        {
          selector: "MemberExpression[property.name='cssText']",
          message: 'Set a custom property with style.setProperty, so the look stays in CSS.',
        },
        // The app may be under a base path (https://example.com/greenglow/), and every page is at
        // it: addresses are relative to the page ('api/me'), so the browser adds the base.
        {
          selector: `Literal[value=${APP_PATH}]`,
          message: APP_PATH_MESSAGE,
        },
        {
          selector: `TemplateLiteral > TemplateElement:first-child[value.raw=${APP_PATH}]`,
          message: APP_PATH_MESSAGE,
        },
      ],
    },
  },
);
