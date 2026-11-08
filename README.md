# Real World Maps: Overlay & Builder

Antes llamado Map Overlay; el nombre interno (`MapOverlay`: DLL, carpeta, ajustes y log) no cambia, para conservar los datos de quienes ya lo usan.

Mod de código para Cities: Skylines II (interfaz en inglés). Genera mapas desde lugares reales en el editor y muestra OpenStreetMap sobre el terreno a escala real (1 m del mundo real = 1 unidad del juego, ajustable), como papel cebolla, para calcar trazados de ciudades reales. No afecta a la partida guardada.

## Novedades de 0.3.0

- **Proyectos**: uno por ciudad; se crean, cargan, guardan y eliminan. Guardan la posición y la vista del mapa, y se pueden bloquear para que el mapa no se mueva. Están en `ModsData\MapOverlay\projects`.
- **Pestaña Terrain (solo en el editor)**: genera el área jugable y el mapa del mundo con elevación real, en el mismo encuadre que el Overlay, con suavizado, mar, lagos, ríos, canales y árboles. El agua se coloca al terminar de generar y las fuentes se crean solas. En partida la pestaña solo muestra un aviso.
- **Agua**: los ríos se dibujan como una red conectada desde su desembocadura, con fondo plano, orillas sobre el agua y una profundidad que crece con el ancho. El log compara el agua real con la planificada a los 60 y 180 s.
- **Panel**: pestañas Overlay y Terrain, estilo del juego y ventana que se arrastra desde el encabezado. En el editor, el botón está en la fila de herramientas de abajo.

Fuentes de datos: elevación de Terrain Tiles en AWS (con la atribución que exige, en Data credits), agua de OpenStreetMap con teselas vectoriales de OpenFreeMap y OpenMapTiles, y árboles de ESA WorldCover 2021 (CC BY 4.0).

## Data credits

- Maps and water: © OpenStreetMap contributors (ODbL), with vector tiles by OpenFreeMap and © OpenMapTiles.
- Topographic map style: © OpenTopoMap (CC-BY-SA).
- Search: Nominatim (OpenStreetMap) and Photon (komoot).
- Trees: © ESA WorldCover project 2021 / Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover consortium (CC BY 4.0).
- Elevation: Terrain Tiles on AWS (Mapzen). ArcticDEM terrain data DEM(s) were created from DigitalGlobe, Inc., imagery and funded under National Science Foundation awards 1043681, 1559691, and 1542736; Australia terrain data © Commonwealth of Australia (Geoscience Australia) 2017; Austria terrain data © offene Daten Österreichs – Digitales Geländemodell (DGM) Österreich; Canada terrain data contains information licensed under the Open Government Licence – Canada; Europe terrain data produced using Copernicus data and information funded by the European Union - EU-DEM layers; Global ETOPO1 terrain data U.S. National Oceanic and Atmospheric Administration; Mexico terrain data source: INEGI, Continental relief, 2016; New Zealand terrain data Copyright 2011 Crown copyright (c) Land Information New Zealand and the New Zealand Government (All rights reserved); Norway terrain data © Kartverket; United Kingdom terrain data © Environment Agency copyright and/or database right 2015. All rights reserved; United States 3DEP (formerly NED) and global GMTED2010 and SRTM terrain data courtesy of the U.S. Geological Survey.

## Uso del Overlay

El botón del mod está en la barra superior izquierda de la partida. Con un proyecto abierto, la pestaña Overlay tiene:

- **Mostrar mapa** y **Opacidad**.
- **Búsqueda**: sugerencias mientras escribes (Photon); Enter busca con Nominatim y, si no encuentra nada, con Photon. También acepta coordenadas (`19.4326, -99.1332`) o un enlace de Google Maps u OpenStreetMap. El lugar elegido aparece donde mira la cámara.
- **Arrastrar en el mapa**: activa una herramienta del juego. Arrastra con clic izquierdo para mover el mapa (el punto que agarras se queda bajo el cursor); con Shift + arrastrar lo giras alrededor del punto que mira la cámara. Clic derecho o Esc para salir.
- **Mover mapa**: las flechas desplazan el mapa según la vista de la cámara, con pasos de 1, 5, 25 o 100 m, o de 1 km. ◎ trae el centro del mapa al punto que mira la cámara.
- **Rotación**: gira el mapa alrededor del punto que mira la cámara.
- **Escala**: 100 % es 1:1. Va de 25 % a 200 %: con el deslizador, con los botones rápidos o escribiendo el «Map size» de Terraining (57.344 km = 100 %, 28.672 km = 200 %, 114.688 km = 50 %). La escala cambia alrededor del punto que mira la cámara.
- **Centro del mapa en el centro de la ciudad**: lleva el centro del mapa al origen del mundo, que es donde Terraining centra el heightmap. Para que coincidan: busca el mismo punto central que usaste en Terraining, pulsa el botón y pon la misma escala y la misma rotación.
- **Estilo de mapa**: OpenStreetMap, Topográfico (OpenTopoMap, con curvas de nivel) o Personalizado (una URL de teselas con `{z}`, `{x}` y `{y}`, por ejemplo de un proveedor donde tengas tu propia clave).
- **Detalle**: nivel de zoom de las teselas (14–15). Solo cambia la nitidez, no la escala. Si el área o una escala pequeña necesitan demasiadas teselas, baja solo (hasta 12) y el panel lo indica.
- **Área**: tamaño del cuadrado de mapa que se carga. Se cargan anillos completos de teselas alrededor de la tesela central, así que cada paso carga algo nuevo; el panel muestra el tamaño real (por ejemplo 1.2, 3.5, 5.8, 8.1 y 10.4 km con detalle 15 en Ciudad de México). Después del último paso está **Mapa completo**, que cubre toda el área jugable (14.3 km). Para no pasar de 900 teselas baja el detalle según la latitud, la rotación y la escala, y avisa antes de hacerlo. En Mapa completo, las teselas que ya están en caché se ven en todo el mapa, pero las nuevas solo se descargan alrededor del punto que mira la cámara (de 2 a 8 km según la distancia de la cámara); el resto se completa al mover la cámara.
- **Encima de todo**: dibuja el mapa sobre edificios y relieve. Desactívalo para que quede pegado al terreno.
- **Cargar donde miras**: al mover la cámara lejos del centro, carga la zona nueva sin desplazar el mapa.

En **Opciones → Map Overlay** están los mismos ajustes, la altura sobre el terreno y el botón para borrar la caché.

Las teselas se guardan en `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\ModsData\MapOverlay\cache` con extensión `.tile`, no `.png`: el juego indexa todas las imágenes de su carpeta de datos y las mostraba en los selectores de imágenes del editor de assets. A los 30 días se pregunta al servidor con `If-Modified-Since`; si no cambiaron, se siguen usando sin volver a descargarlas. La caché `.png` de la versión 0.1 (`ModsData\MapOverlay\tiles`) se borra sola al cargar el mod.

Log: `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\MapOverlay.log`

## Compilar e instalar

Cierra el juego y ejecuta:

```powershell
cd MapOverlay
dotnet build -c Release
```

La compilación hace tres cosas:

1. Compila la interfaz (`UI`, React) con `npm run build` en `UI\build`. Si falta `node_modules`, primero ejecuta `npm install`.
2. Compila la DLL.
3. Copia todo a `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\MapOverlay`.

## Cómo funciona

- `GeoMath` convierte entre teselas Web Mercator y un plano local este/norte en metros reales alrededor del centro del mapa (radios WGS84), así que la escala es 1:1 en cualquier latitud. `MapFrame` coloca ese plano en el mundo: un punto de anclaje (X, Z) y una rotación.
- `OverlayController` crea una malla de 16×16 celdas por tesela que sigue la altura del terreno (`TerrainSystem` y `TerrainUtils.SampleHeight`). Las alturas se recalculan poco a poco en cada fotograma, así que el mapa sigue al terreno si lo modificas.
- Cada tesela usa `HDRP/Unlit`. El juego no incluye la variante transparente de ese shader, así que la opacidad se hace en dos pasadas que no leen alfa: una oscurece el fondo (`dst * (1 - a)`) y otra suma el mapa (`+ mapa * a`). «Encima de todo» cambia la prueba de profundidad a `Always`.
- Las descargas usan `UnityWebRequest`, con 2 conexiones como máximo y una pausa de 60 s si el servidor responde 403 o 429.
- `MapOverlayUISystem` publica el estado para el panel con los bindings del grupo `mapOverlay`.
- El panel usa un kit propio (`UI/src/kit`): tokens en `_tokens.scss` y componentes (panel, secciones plegables, interruptores, deslizador, campos de texto…) en `index.tsx`. Para otro mod basta copiar la carpeta y cambiar `$accent`. Las clases CSS llevan el id del mod como prefijo, así dos mods con el mismo kit no chocan.

## Condiciones de uso de los mapas

- Los datos son © colaboradores de OpenStreetMap (ODbL). El panel muestra la atribución.
- Los servidores de teselas de openstreetmap.org son para uso moderado, sin garantías, y prohíben la descarga masiva (pedir teselas que el usuario no está viendo, sobre todo desde zoom 14) y el uso sin conexión. El mod cumple lo técnico: User-Agent propio, caché de más de 7 días y peticiones condicionales. Para no hacer descarga masiva, **Mapa completo** solo descarga las teselas cercanas al punto que mira la cámara.
- OpenTopoMap también funciona con servidores pequeños y pide un uso moderado.
- CARTO exige una clave desde septiembre de 2026, así que no se incluye. Para mapas sin etiquetas, satélite o mucho uso, lo correcto es **Personalizado** con la clave del propio jugador (MapTiler, Stadia, Thunderforest…), que acepta las condiciones de su proveedor.
- Leaflet no cambiaría nada: es una librería para mostrar mapas en páginas web, y las teselas seguirían viniendo del mismo servidor.
- Nominatim admite como máximo una petición por segundo y no permite autocompletar; por eso las sugerencias usan Photon (komoot), con una pausa de 350 ms al escribir.

## Licencia

El código está bajo la licencia [MIT](LICENSE). Los datos de los mapas no forman parte del código y tienen sus propias condiciones (ver arriba).
