// Манифест Remote Component для веб-клиента Directum RX.
// Один контрол ArmGovDashboard, загрузчик в обложке (scope Cover).
module.exports = {
  vendorName: 'DirRX',
  componentName: 'ArmGovDash',
  componentVersion: '1.0',
  controls: [
    {
      name: 'ArmGovDashboard',
      loaders: [
        { name: 'armgov-dashboard-cover-loader', scope: 'Cover' }
      ]
    }
  ]
};
