import js from '@eslint/js';
import noUnsanitizedPlugin from 'eslint-plugin-no-unsanitized';
import prettierRecommended from 'eslint-plugin-prettier/recommended';
import reactPlugin from 'eslint-plugin-react';
import reactHooksPlugin from 'eslint-plugin-react-hooks';
import tseslint from 'typescript-eslint';

/** ESLint flat-config для Remote Component (чеклист платформы Directum RX). */
export default tseslint.config(
  {
    ignores: [
      'dist/**',
      'node_modules/**',
      'tools/assets/**',
      '*.js',
      'public-path.js',
      'component.manifest.js',
      'webpack.config.js',
    ],
  },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  prettierRecommended,
  reactPlugin.configs.flat['jsx-runtime'],
  {
    files: ['**/*.{ts,tsx}'],
    plugins: {
      'react': reactPlugin,
      'react-hooks': reactHooksPlugin,
      'no-unsanitized': noUnsanitizedPlugin,
    },
    languageOptions: {
      parserOptions: {
        project: './tsconfig.json',
      },
    },
    settings: {
      react: { version: 'detect' },
    },
    rules: {
      'no-unsanitized/method': 'error',
      'no-unsanitized/property': 'error',
      '@typescript-eslint/no-explicit-any': 'error',
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_' },
      ],
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'warn',
      'no-console': ['warn', { allow: ['warn', 'error'] }],
    },
  },
);
