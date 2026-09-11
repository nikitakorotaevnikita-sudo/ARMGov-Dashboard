// Единый реестр загрузчиков — хост запрашивает его как модуль `loaders` из контейнера.
// Ключ = имя loader-а из component.manifest.js. Значение = IRemoteControlLoader ({ default }).
import { makeWidgetLoader } from './src/loaders/widget-loader';
import { ArmDashboardWidget } from './src/dashboard/widget';

const loaders = {
  'arm-dashboard-loader': { default: makeWidgetLoader(ArmDashboardWidget) },
};

export default loaders;
