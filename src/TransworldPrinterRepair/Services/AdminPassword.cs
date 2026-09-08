using System.Security.Cryptography;
using System.Text;

namespace Transworld.PrinterRepair.Services;

/// <summary>
/// Puerta de acceso al panel administrativo.
///
/// AVISO DE SEGURIDAD: una contrasena incrustada en un ejecutable portable NO es un secreto.
/// Cualquiera que tenga el archivo puede extraerla con esfuerzo moderado. Esto disuade a un
/// trabajador curioso, no a alguien decidido. Se guarda un hash PBKDF2 en vez del texto plano
/// para que la contrasena no aparezca al inspeccionar las cadenas del binario, y la comparacion
/// es en tiempo constante, pero eso sigue siendo ofuscacion, no proteccion real.
///
/// Si en el futuro hace falta seguridad de verdad, la via correcta es volver a la elevacion UAC:
/// ahi la autenticacion la hace Windows contra las credenciales reales del dominio.
/// </summary>
public static class AdminPassword
{
    private const int Iterations = 120_000;

    private static readonly byte[] Salt = Convert.FromHexString(
        "5472616E73776F726C64505232303236");

    private static readonly byte[] Expected = Convert.FromHexString(
        "40561C72B2AC21B6C760E963105059AB0968D610C10096C0E77F325F8BDBA96F");

    public static bool Verify(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(candidate),
            Salt,
            Iterations,
            HashAlgorithmName.SHA256,
            Expected.Length);

        // Comparacion en tiempo constante: no filtra cuantos caracteres se acertaron.
        return CryptographicOperations.FixedTimeEquals(actual, Expected);
    }
}
