const path = require('path');
const { ModuleFederationPlugin } = require('webpack').container;
// Плагин платформы: генерирует metadata.json по component.manifest.js
const MetadataPlugin = require('@directum/sungero-remote-component-metadata-plugin');

module.exports = (env = {}) => ({
  entry: {},
  mode: env.mode === 'standalone' ? 'development' : 'production',
  output: {
    path: path.resolve(__dirname, 'dist'),
    publicPath: 'auto',
    clean: true,
  },
  resolve: { extensions: ['.tsx', '.ts', '.js'] },
  module: {
    rules: [
      {
        test: /\.(ts|tsx)$/,
        exclude: /node_modules/,
        use: {
          loader: 'babel-loader',
          options: { presets: ['@babel/preset-react', '@babel/preset-typescript'] },
        },
      },
    ],
  },
  plugins: [
    new ModuleFederationPlugin({
      name: 'ArmGovDash',
      filename: 'remoteEntry.js',
      exposes: {
        // Загрузчик обложки — точка монтирования для хоста
        './armgov-dashboard-cover-loader': './src/loaders/armgov-dashboard-cover-loader',
      },
      // React — singleton из хоста (требование платформы; не дублировать react в RC)
      shared: {
        react: { singleton: true, requiredVersion: '18.2.0' },
        'react-dom': { singleton: true, requiredVersion: '18.2.0' },
      },
    }),
    new MetadataPlugin({ manifest: './component.manifest.js' }),
  ],
});
