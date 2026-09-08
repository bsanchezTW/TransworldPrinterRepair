# AutoReparación de Impresoras Transworld

Herramienta portable para Windows 10 y 11 (x64) que permite a un trabajador sin conocimientos
técnicos dejar operativa la impresora de su área en tres clics, sin tocar el Panel de control,
PowerShell ni Dispositivos e impresoras.

```
Abrir TransworldPrinterRepair.exe → Elegir área → "No puedo imprimir" → Reparar → Imprimir
```

---

## Entregable

Un **único archivo**: `TransworldPrinterRepair.exe` (~250 MB).

- No tiene instalador. Se copia y se ejecuta.
- No necesita .NET Runtime, PowerShell adicional ni Visual C++ Redistributable.
- No se registra como servicio, no arranca con Windows y no deja procesos residentes.
- Los 5 paquetes de controlador van compilados dentro del ejecutable.

---

## Áreas configuradas

| Área | IP | Impresora resultante | Modelo |
|---|---|---|---|
| Gerencia | 192.168.190.8 | `IMPRESORA GERENCIA` | Xerox WorkCentre 5330 |
| Finanzas | 192.168.190.4 | `IMPRESORA FINANZAS` | HP LaserJet MFP M426fdw |
| Facturación | 192.168.190.12 | `IMPRESORA FACTURACION` | HP LaserJet MFP M426fdw |
| Logística | 192.168.190.15 | `IMPRESORA LOGISTICA` | HP LaserJet MFP M426fdw |
| RRHH | 192.168.190.7 | `IMPRESORA RRHH` | HP LaserJet Pro MFP M428fdw |
| Bodega | 192.168.190.21 | `IMPRESORA BODEGA` | HP Laser MFP 137fnw |
| Bodega Chica | 192.168.190.13 | `IMPRESORA BODEGA CHICA` | HP Laser MFP 137fnw |
| Ventas | 192.168.190.5 | `IMPRESORA VENTAS` | HP LaserJet M402dne |

El nombre de la impresora se genera solo a partir del área (`IMPRESORA [ÁREA]`, sin tildes);
nadie lo escribe a mano. Las áreas se leen de `Resources/areas.json`, nunca están escritas
en la interfaz.

---

## Qué hace la reparación

Once pasos, todos idempotentes: ejecutarla varias veces deja siempre el mismo resultado,
nunca `IMPRESORA GERENCIA (2)`.

1. Comprueba y arranca el servicio de impresión.
2. Enumera las impresoras del equipo y las clasifica.
3. Elimina las impresoras físicas, de red y duplicadas. **Conserva** Microsoft Print to PDF,
   XPS, Fax y OneNote: borrarlas no repara nada y rompe flujos del usuario.
4. Elimina los puertos TCP/IP que quedan huérfanos y los duplicados de la IP del área.
5. Extrae el paquete de controlador embebido a una caché en ProgramData.
6. Lo almacena en el Driver Store (`pnputil /add-driver /install`).
7. Lo publica en el spooler (`InstallPrinterDriverFromPackage`).
8. Crea o reconfigura el puerto `IP_<dirección>` en **RAW 9100 con SNMP desactivado**.
9. Crea la impresora con el nombre del área.
10. Comprueba que la impresora responde por TCP.
11. Relee la configuración del sistema y verifica que coincide con lo esperado.

Al terminar, el proceso **no elevado** marca la impresora como predeterminada y desactiva
"Permitir que Windows administre mi impresora predeterminada".

### Por qué SNMP se desactiva

Con SNMP activo Windows marca la impresora como "Sin conexión" en cuanto una consulta falla,
aunque la impresora imprima perfectamente. Es la causa más frecuente de la avería que esta
herramienta viene a reparar.

### Por qué RAW 9100 y no LPR 515

RAW es más simple y fiable. Los ocho modelos lo aceptan. Los puertos que se encuentren
configurados como LPR se reconfiguran a RAW.

---

## Permisos

La aplicación arranca **sin elevar**: abrir el `.exe` no muestra ningún UAC.

Al pulsar *Reparar impresora* se lanza el mismo ejecutable en modo worker con el verbo
`runas`, y Windows muestra **un solo diálogo UAC**. El worker informa del progreso real a la
interfaz por un named pipe y termina. La ventana principal no se reinicia ni parpadea.

Marcar la impresora como predeterminada es una preferencia **por usuario**, así que se aplica
desde el proceso no elevado: si el UAC se aceptara con las credenciales de otro administrador,
hacerlo desde el worker lo aplicaría a ese otro perfil y el trabajador no vería el cambio.

---

## Dónde se guarda cada cosa

**Dentro del `.exe`** (inmutable, viaja con el binario):

- `areas.json` — las 8 áreas y los 5 paquetes de controlador.
- Los 5 `.zip` de controlador.

Por eso el ejecutable funciona recién copiado en cualquier PC, sin archivos acompañantes.

**En `C:\ProgramData\Transworld\PrinterRepair\`** (mutable, por equipo):

```
logs\repair-AAAAMMDD.log          Registro técnico completo
logs\autodiagnostico-*.txt        Informes de --selftest
overrides.json                    IPs corregidas por el administrador
state.json                        Última área usada, para destacarla
cache\drivers\<paquete>\<hash>\   Controlador extraído, reutilizado entre ejecuciones
```

**Nada se escribe junto al `.exe`**: el usuario puede ejecutarlo desde Descargas, un USB o una
ruta de red sin permiso de escritura.

---

## Configuración administrativa

El botón ⚙ de la pantalla inicial abre el panel en una **instancia elevada aparte**. El UAC de
Windows es la autenticación; no hay ninguna contraseña dentro del programa.

Permite:

- Corregir la dirección IP de cada área (se guarda en `overrides.json` y tiene prioridad sobre
  la de fábrica).
- Probar la conexión con cada impresora.
- Restaurar la IP original.
- Consultar el estado del equipo y abrir la carpeta de registros.

**No permite añadir áreas ni sustituir controladores**: van compilados en el ejecutable, así
que eso exige regenerar el `.exe` (ver más abajo).

---

## Compilar

Requisitos: **.NET SDK 10** (`winget install Microsoft.DotNet.SDK.10`).

```powershell
.\tools\build.ps1
```

El resultado queda en `publish\TransworldPrinterRepair.exe`.

Los `.zip` de controlador **no se versionan en git** (90 MB). El script los copia desde
OneDrive antes de compilar; si están en otra ruta:

```powershell
.\tools\build.ps1 -DriversSource "D:\Controladores"
```

### Cambiar áreas, IPs o controladores de fábrica

1. Editar `src/TransworldPrinterRepair/Resources/areas.json`.
2. Dejar el nuevo `.zip` en la carpeta de controladores.
3. Volver a ejecutar `tools\build.ps1`.

El campo `driverName` debe coincidir **carácter a carácter** con el nombre que el INF publica
en el spooler. Es el error número uno al añadir un modelo. Para averiguarlo, buscar en el
`.inf` la cadena del modelo (sección `[Strings]` o la línea `"Nombre" = SECCION,HWID`).

---

## Diagnóstico

```powershell
TransworldPrinterRepair.exe --selftest
```

Comprobación **no destructiva**: valida el entorno, enumera impresoras, puertos y
controladores, y hace una prueba de creación y borrado de un puerto y una impresora
temporales sobre `192.0.2.250` (IP reservada por la RFC 5737, nunca corresponde a un equipo
real). No toca nada del equipo y deja un informe en la carpeta de registros.

Ejecutado como administrador hace la prueba completa; sin elevar, omite esa parte.

---

## Aviso de SmartScreen y antivirus

El ejecutable **no está firmado digitalmente**. La primera vez que se abra, Windows mostrará:

> Windows protegió su PC

Hay que pulsar **Más información** → **Ejecutar de todas formas**.

Como además manipula controladores de impresora, algunos antivirus corporativos pueden
marcarlo. Recomendaciones para TI:

- Añadir una exclusión por ruta o por hash en la consola del antivirus.
- O distribuirlo desde un recurso de red marcado como zona de confianza, lo que evita el aviso.
- O adquirir un certificado de firma de código y firmarlo con `signtool` (el script de
  publicación admite añadir ese paso).

---

## Arquitectura

```
Presentation/   MainWindow, AdminWindow, Views, ViewModels, Styles
      ↓
Services/       PrinterRepairService, PrinterDiscoveryService, DriverService,
                PortApi (vía Infrastructure), NetworkService, ConfigurationService,
                LoggingService, RepairCoordinator
      ↓
Infrastructure/ PrinterApi, PortApi, SpoolerControl, ProcessRunner, ProgressChannel,
                ElevatedLauncher, SystemChecks, Interop/NativeMethods
```

La interfaz nunca ejecuta comandos del sistema: solo habla con `Services/`, que a su vez
delega en `Infrastructure/`.

### Notas técnicas

- **WPF, no WinUI 3**: WinUI 3 sin empaquetar necesita el bootstrapper del Windows App SDK y
  tiene problemas conocidos con `PublishSingleFile`.
- **P/Invoke a `winspool.drv`** en lugar de PowerShell: sin dependencias en el equipo destino
  y con códigos de error distinguibles.
- **Puertos**: se leen del registro del monitor (fuente exacta y estable) y se escriben con
  `XcvData` (`AddPort`, `ConfigPort`, `DeletePort` con `DELETE_PORT_DATA_1`).
- **Windows Protected Print Mode**: si está activo (Windows 11 24H2+), Windows rechaza los
  controladores de terceros. La aplicación lo detecta y recurre al *Microsoft IPP Class Driver*
  en lugar de fallar con un error opaco.
- **4 de los 5 controladores son v4**; solo el del HP Laser 137fnw es v3.
- Los paquetes traen INF de escáner y stubs USB: se instala **el INF concreto** de cada área,
  nunca `*.inf`.
- Todos los argumentos de proceso se pasan por `ArgumentList`, nunca concatenando cadenas.

---

## Errores frecuentes

| Mensaje al usuario | Causa real | Qué hacer |
|---|---|---|
| No fue posible comunicarse con la impresora | La impresora está apagada o fuera de red | Encenderla y reintentar |
| Windows tiene activada una protección… | Protected Print Mode | Desactivarlo por GPO, o usar el driver IPP |
| No se concedieron los permisos necesarios | El usuario canceló el UAC | Reintentar y aceptar |
| El servicio de impresión no responde | Spooler dañado | Reiniciar el equipo |

Los códigos técnicos (HRESULT, exit codes) nunca se muestran al trabajador: van al log.
