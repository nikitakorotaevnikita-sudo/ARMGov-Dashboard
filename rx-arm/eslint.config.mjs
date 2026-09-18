import js from '@eslint/js';
import checkFilePlugin from 'eslint-plugin-check-file';
import cssModulesPlugin from 'eslint-plugin-css-modules';
import noUnsanitizedPlugin from 'eslint-plugin-no-unsanitized';
import prettierRecommended from 'eslint-plugin-prettier/recommended';
import reactPlugin from 'eslint-plugin-react';
import reactHooksPlugin from 'eslint-plugin-react-hooks';
import eslintUnicornPlugin from 'eslint-plugin-unicorn';
import eslintUnusedImportsPlugin from 'eslint-plugin-unused-imports';
import tseslint from 'typescript-eslint';

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
      'tests/**',
      'jest.config.js',
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
      'unicorn': eslintUnicornPlugin,
      'unused-imports': eslintUnusedImportsPlugin,
      'check-file': checkFilePlugin,
      'css-modules': cssModulesPlugin,
    },
    languageOptions: {
      parserOptions: { project: './tsconfig.json' },
    },
    settings: { react: { version: 'detect' } },
    rules: {
      'unicorn/better-regex': 'warn',
      'unicorn/catch-error-name': 'error',
      'unicorn/error-message': 'error',
      'unicorn/explicit-length-check': 'error',
      'unicorn/new-for-builtins': 'error',
      'unicorn/no-array-for-each': 'off',
      'unicorn/prefer-string-slice': 'error',
      'unicorn/throw-new-error': 'error',
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'error',
      '@typescript-eslint/no-explicit-any': 'error',
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_' },
      ],
      '@typescript-eslint/no-non-null-assertion': 'error',
      '@typescript-eslint/consistent-type-definitions': ['error', 'interface'],
      'no-console': ['error', { allow: ['warn', 'error'] }],
      'no-debugger': 'error',
      'no-eval': 'error',
      'eqeqeq': ['error', 'smart'],
      'prefer-const': 'error',
      'no-new-func': 'error',
      'no-script-url': 'error',
      '@typescript-eslint/no-implied-eval': 'error',
      'react/no-danger': 'error',
      'react/no-danger-with-children': 'error',
      'react/iframe-missing-sandbox': 'error',
      'no-unsanitized/method': 'error',
      'no-unsanitized/property': 'error',
      'react/jsx-no-leaked-render': 'error',
      'react/jsx-key': ['error', { checkFragmentShorthand: true }],
      'react/jsx-boolean-value': ['error', 'always'],
      'unused-imports/no-unused-imports': 'error',
      'prettier/prettier': 'warn',
      'css-modules/no-undef-class': ['error', { camelCase: true }],
      'check-file/filename-naming-convention': [
        'error',
        { 'src/**/*.{ts,tsx}': 'KEBAB_CASE' },
        { ignoreMiddleExtensions: true },
      ],
      'check-file/folder-naming-convention': ['error', { 'src/**/': 'KEBAB_CASE' }],
    },
  },
);
