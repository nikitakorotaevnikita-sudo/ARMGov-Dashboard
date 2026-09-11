// Манифест стороннего компонента (Remote Component) для Directum RX-веб.
// АРМ руководителя, 1-й этап MVP: ОДИН контрол — весь экран целиком (четыре блока
// в 12-колоночной сетке), а не по контролу на блок. Так лэйаут макета сохраняется
// один в один и не зависит от того, как редактор обложки разложит контролы по колонкам.
// Имя loader-а = ключ в реестре component.loaders.ts.
// metadata.json генерируется из этого файла плагином при сборке.
module.exports = {
  vendorName: 'ARMGov',
  componentName: 'LeaderCover',
  componentVersion: '1.0',
  hostApiVersion: '1.0.1',
  controls: [
    {
      id: 'b4e8b293-dcfa-4250-84d1-df6a575bacfd',
      name: 'LeaderDashboard',
      loaders: [{ name: 'arm-dashboard-loader', scope: 'Cover' }],
      displayNames: [
        { locale: 'ru', name: 'АРМ руководителя — обзор поручений' },
        { locale: 'en', name: 'Leader workplace — orders overview' },
      ],
    },
  ],
};
