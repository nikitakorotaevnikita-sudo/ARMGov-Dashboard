// webpack.config.js — Webpack 5 Module Federation для Directum RX Remote Component (Cover).
// Канон: sungero-remote-component-example-react (deploy path, MiniCss prepend, CSS Modules).
//
// WORKAROUND-05: по умолчанию react НЕ в shared (хост RX 26.2 → React 17).
// Включить shared: ARMGOV_SHARED_REACT=1 в .env после проверки версии хоста ≥18.
require('dotenv').config();

const path = require('path');
const webpack = require('webpack');
const HtmlWebpackPlugin = require('html-webpack-plugin');
const MiniCssExtractPlugin = require('mini-css-extract-plugin');
const TerserPlugin = require('terser-webpack-plugin');
const CssMinimizerPlugin = require('css-minimizer-webpack-plugin');
const { dependencies } = require('./package.json');

const _pluginPkg = require('@directum/sungero-remote-component-metadata-plugin');
const SungeroRemoteComponentMetadataPlugin =
  _pluginPkg.SungeroRemoteComponentMetadataPlugin || _pluginPkg.default || _pluginPkg;

const manifest = require('./component.manifest.js');

const remoteEntryName = `${manifest.vendorName}_${manifest.componentName}_${manifest.componentVersion.replace(/\./g, '_')}`;
const shareReact = process.env.ARMGOV_SHARED_REACT === '1';

function resolveOutputPath(isStandalone, isProduction) {
  if (
    !isStandalone &&
    !isProduction &&
    process.env.SUNGERO_DEPLOY_BASE &&
    process.env.SUNGERO_SOLUTION_NAME &&
    process.env.SUNGERO_COMPONENT_NAME
  ) {
    return path.resolve(
      process.env.SUNGERO_DEPLOY_BASE,
      `${process.env.SUNGERO_SOLUTION_NAME}.Components`,
      process.env.SUNGERO_COMPONENT_NAME,
    );
  }
  return path.resolve(__dirname, 'dist');
}

module.exports = (env, argv) => {
  const isProduction = argv.mode === 'production';
  const isStandalone = Boolean(env && env.mode === 'standalone');
  const extractCss = !isStandalone;
  const cssLoader = extractCss ? MiniCssExtractPlugin.loader : 'style-loader';

  const federationShared = shareReact
    ? {
        'react': { requiredVersion: dependencies.react },
        'react-dom': { requiredVersion: dependencies['react-dom'] },
      }
    : undefined;

  return {
    entry: isStandalone
      ? { index: './index.js' }
      : {
          index: './index.js',
          [remoteEntryName]: './public-path.js',
        },
    output: {
      path: resolveOutputPath(isStandalone, isProduction),
      filename: isProduction
        ? `[name]_${manifest.componentVersion.replace(/\./g, '_')}_[contenthash:8].js`
        : `[name]_${manifest.componentVersion.replace(/\./g, '_')}.js`,
      chunkFilename: isProduction
        ? `chunks/[id]_${manifest.componentVersion.replace(/\./g, '_')}_[contenthash:8].js`
        : `chunks/[id]_${manifest.componentVersion.replace(/\./g, '_')}.js`,
      publicPath: '',
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
          test: /\.module\.css$/,
          use: [
            cssLoader,
            {
              loader: 'css-loader',
              options: {
                modules: {
                  localIdentName: isProduction
                    ? '[hash:base64:8]'
                    : '[name]__[local]--[hash:base64:5]',
                  namedExport: false,
                },
              },
            },
          ],
        },
        {
          test: /\.css$/,
          exclude: /\.module\.css$/,
          use: [cssLoader, 'css-loader'],
        },
        {
          test: /\.(png|jpg|jpeg|gif)$/,
          type: 'asset/resource',
          generator: {
            filename: `images/[name]_${manifest.componentVersion}[ext]`,
          },
        },
        {
          test: /\.svg$/,
          type: 'asset/inline',
        },
      ],
    },
    plugins: isStandalone
      ? [new HtmlWebpackPlugin({ template: './public/index.html' })]
      : [
          new MiniCssExtractPlugin({
            filename: isProduction ? 'css/[name].[contenthash:8].css' : 'css/[name].css',
            insert: linkTag => document.head.prepend(linkTag),
          }),
          new webpack.container.ModuleFederationPlugin({
            name: remoteEntryName,
            filename: 'remoteEntry.js',
            exposes: {
              loaders: './component.loaders.ts',
              publicPath: './public-path.js',
            },
            ...(federationShared ? { shared: federationShared } : {}),
          }),
          new SungeroRemoteComponentMetadataPlugin(manifest),
        ],
    optimization: {
      moduleIds: 'deterministic',
      minimizer: [
        new TerserPlugin({
          parallel: true,
          terserOptions: {
            mangle: true,
            format: { comments: false },
          },
          extractComments: false,
        }),
        new CssMinimizerPlugin({
          minimizerOptions: {
            preset: [
              'default',
              {
                discardComments: { removeAll: true },
                colormin: false,
              },
            ],
          },
        }),
      ],
    },
    devtool: isProduction ? 'nosources-source-map' : 'eval-source-map',
  };
};
