// webpack.config.js — Webpack 5 Module Federation для Directum RX Remote Component (Cover scope).
// Канон: knowledge-base/patterns/vendor-frontend/02-build-webpack-mf.md +
//        .claude/skills/implementor-kb/assets/rc/reference-examples/webpack.config.js
// Портировано из omni-cover-employee/webpack.config.js — тот же канон сборки, другой хост/визуал.
//
// Режимы:
//   release            → webpack --mode production               (MF + metadata + минификация → dist/)
//   dev:remote         → webpack --mode development               (MF-сборка)
//   dev:standalone     → webpack --mode development --env mode=standalone (SPA-превью, без MF)
//
// react/react-dom НЕ шарим с хостом: RX-веб (26.2.0.0068) сам singleton'ит React 17.0.2,
// а наш код собран на 18 (createRoot из react-dom/client — API, которого в 17 нет). При
// shared+singleton MF предупреждает про несовпадение версий, но всё равно отдаёт версию
// хоста — react-dom/client.createRoot резолвится в undefined → падение в рантайме
// («(0, n.s) is not a function»). Раз хост не даёт совместимую версию — носим свою React
// 18 бандлом внутри RC, изолированно от React-дерева хоста (мы монтируемся в свой
// контейнер через createRoot, в DOM хоста не заходим — изоляция безопасна).
const path = require('path');
const webpack = require('webpack');
const HtmlWebpackPlugin = require('html-webpack-plugin');
const MiniCssExtractPlugin = require('mini-css-extract-plugin');
const TerserPlugin = require('terser-webpack-plugin');
const CssMinimizerPlugin = require('css-minimizer-webpack-plugin');

// Плагин метаданных может экспортироваться по-разному (default / named / direct) — fallback-паттерн.
const _pluginPkg = require('@directum/sungero-remote-component-metadata-plugin');
const SungeroRemoteComponentMetadataPlugin =
  _pluginPkg.SungeroRemoteComponentMetadataPlugin || _pluginPkg.default || _pluginPkg;

const manifest = require('./component.manifest.js');

// publicName = `${vendorName}_${componentName}_${version с '.'→'_'}` → имя MF-контейнера.
const remoteEntryName = `${manifest.vendorName}_${manifest.componentName}_${manifest.componentVersion.replace(/\./g, '_')}`;

module.exports = (env, argv) => {
  const isProduction = argv.mode === 'production';
  const isStandalone = env && env.mode === 'standalone';

  // Канон платформы (vendor-frontend/02): хост запрашивает у контейнера единый
  // реестр `loaders` (component.loaders.ts) + `publicPath`. НЕ пер-loader модули —
  // иначе хост падает с «Module "loaders" does not exist in container».
  const exposes = {
    loaders: './component.loaders.ts',
    publicPath: './public-path.js',
  };

  const config = {
    entry: isStandalone
      ? { index: './index.js' }
      : {
          index: './index.js',
          // dual entry — обязательно для Module Federation runtime
          [remoteEntryName]: './public-path.js',
        },
    output: {
      path: path.resolve(__dirname, 'dist'),
      filename: isProduction
        ? `[name]_${manifest.componentVersion.replace(/\./g, '_')}_[contenthash:8].js`
        : '[name].js',
      chunkFilename: isProduction
        ? `chunks/[id]_${manifest.componentVersion.replace(/\./g, '_')}_[contenthash:8].js`
        : 'chunks/[id].js',
      publicPath: 'auto',
      clean: true,
    },
    resolve: {
      extensions: ['.tsx', '.ts', '.jsx', '.js'],
    },
    module: {
      rules: [
        {
          test: /\.(ts|tsx|js|jsx)$/,
          exclude: /node_modules/,
          use: {
            loader: 'babel-loader',
            options: {
              presets: [
                ['@babel/preset-react', { runtime: 'automatic' }],
                '@babel/preset-typescript',
              ],
            },
          },
        },
        {
          test: /\.css$/,
          use: [
            isProduction ? MiniCssExtractPlugin.loader : 'style-loader',
            'css-loader',
          ],
        },
        {
          test: /\.(png|jpg|jpeg|gif|svg)$/,
          type: 'asset/resource',
        },
      ],
    },
    plugins: [
      ...(isStandalone
        ? [new HtmlWebpackPlugin({ template: './public/index.html' })]
        : [
            new webpack.container.ModuleFederationPlugin({
              name: remoteEntryName,
              filename: 'remoteEntry.js',
              exposes,
            }),
            new SungeroRemoteComponentMetadataPlugin(manifest),
            ...(isProduction
              ? [new MiniCssExtractPlugin({ filename: 'css/[name].[contenthash:8].css' })]
              : []),
          ]),
    ],
    optimization: {
      moduleIds: 'deterministic',
      minimizer: [new TerserPlugin(), new CssMinimizerPlugin()],
    },
    devServer: {
      port: 3002,
      hot: true,
      static: { directory: path.resolve(__dirname, 'dist') },
      headers: { 'Access-Control-Allow-Origin': '*' },
    },
    devtool: isProduction ? 'nosources-source-map' : 'eval-source-map',
  };

  return config;
};
