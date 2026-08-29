// Flat ESLint config for the Vue 3 + TypeScript client.
// Uses eslint, typescript-eslint (@typescript-eslint parser + plugin) and
// eslint-plugin-vue. Run with `npm run lint`.
import js from '@eslint/js';
import tseslint from 'typescript-eslint';
import pluginVue from 'eslint-plugin-vue';
import globals from 'globals';

export default tseslint.config(
  {
    // Build output, deps, generated reports and the standalone embed script.
    ignores: [
      'dist/**',
      'node_modules/**',
      'coverage/**',
      'playwright-report/**',
      'test-results/**',
      'public/**',
      '*.timestamp-*',
    ],
  },

  js.configs.recommended,
  ...tseslint.configs.recommended,
  ...pluginVue.configs['flat/recommended'],

  {
    // <script lang="ts"> blocks inside .vue files are parsed by the TS parser
    // (eslint-plugin-vue sets vue-eslint-parser as the top-level parser).
    files: ['**/*.vue'],
    languageOptions: {
      parserOptions: {
        parser: tseslint.parser,
      },
    },
  },

  {
    // App source runs in the browser; scope the TS-specific rule tweak to files
    // where the @typescript-eslint plugin is actually registered.
    files: ['**/*.{ts,mts,cts,tsx,vue}'],
    languageOptions: {
      ecmaVersion: 'latest',
      sourceType: 'module',
      globals: {
        ...globals.browser,
      },
    },
    rules: {
      // Allow intentionally-unused args/vars when prefixed with `_`.
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_', caughtErrors: 'none' },
      ],
    },
  },

  {
    // Vue template *formatting* rules are stylistic (formatter territory) and
    // produced hundreds of non-actionable warnings across the existing templates.
    // Disable them so `npm run lint` surfaces real correctness issues, not layout.
    files: ['**/*.vue'],
    rules: {
      'vue/max-attributes-per-line': 'off',
      'vue/singleline-html-element-content-newline': 'off',
      'vue/multiline-html-element-content-newline': 'off',
      'vue/html-self-closing': 'off',
      'vue/attributes-order': 'off',
      'vue/attribute-hyphenation': 'off',
      'vue/html-indent': 'off',
      'vue/first-attribute-linebreak': 'off',
      'vue/html-closing-bracket-newline': 'off',
      'vue/html-quotes': 'off',
    },
  },

  {
    // Unit tests run under Vitest (Node + browser globals).
    files: ['**/*.{test,spec}.{ts,tsx}'],
    languageOptions: {
      globals: {
        ...globals.node,
      },
    },
  },
);
