// =============================================================================
//  PrZip — Patagonia Robot Archive Engine
//  Lee y escribe 7z; abre RAR, TAR, GZip, BZip2, XZ e ISO 9660.
// =============================================================================
//
//  Copyright (c) 2026 Claudio Andino
//  Developed under the Patagonia Robot initiative
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at:
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//
// =============================================================================

namespace PrZip.Exceptions;

/// <summary>
/// Excepción base para todos los errores generados por el motor PrZip.
/// </summary>
public class PrZipException : Exception
{
    public PrZipException()
    {
    }

    public PrZipException(string message) : base(message)
    {
    }

    public PrZipException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Se produce cuando los datos del archivo no cumplen con la especificación 7z, están corrompidos o son inconsistentes.
/// </summary>
public class CorruptArchiveException : PrZipException
{
    public CorruptArchiveException() : base("El archivo 7z está dañado o no cumple con el formato esperado.")
    {
    }

    public CorruptArchiveException(string message) : base(message)
    {
    }

    public CorruptArchiveException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Se produce cuando el archivo finaliza prematuramente mientras se esperaban más datos de cabecera o de flujo.
/// </summary>
public class ArchiveTruncatedException : CorruptArchiveException
{
    public ArchiveTruncatedException() : base("El archivo está truncado: finalizó inesperadamente.")
    {
    }

    public ArchiveTruncatedException(string message) : base(message)
    {
    }

    public ArchiveTruncatedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Se produce cuando el archivo contiene un método de compresión, filtro o propiedad no compatible.
/// </summary>
public class UnsupportedMethodException : PrZipException
{
    public UnsupportedMethodException(string message) : base(message)
    {
    }

    public UnsupportedMethodException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Se produce cuando los datos descifrados son inválidos o la contraseña proporcionada es incorrecta.
/// (En 7z AES-CBC sin autenticación, no se puede distinguir matemáticamente entre contraseña incorrecta y datos corruptos).
/// </summary>
public class InvalidPasswordOrCorruptDataException : PrZipException
{
    public InvalidPasswordOrCorruptDataException() : base("Contraseña incorrecta o datos cifrados corruptos.")
    {
    }

    public InvalidPasswordOrCorruptDataException(string message) : base(message)
    {
    }

    public InvalidPasswordOrCorruptDataException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// La contraseña es incorrecta, comprobado: el formato guarda un valor de chequeo de la contraseña (RAR5) y no coincide.
/// A diferencia de <see cref="InvalidPasswordOrCorruptDataException"/>, de la que hereda para no romper a quien ya la
/// atrapa, acá no hay ambigüedad con datos dañados.
/// </summary>
public class PasswordIncorrectException : InvalidPasswordOrCorruptDataException
{
    public PasswordIncorrectException(string message) : base(message)
    {
    }
}

/// <summary>
/// Se produce cuando una operación excede los límites de seguridad o memoria configurados (defensa ante bombas de descompresión).
/// </summary>
public class LimitExceededException : PrZipException
{
    public LimitExceededException(string message) : base(message)
    {
    }

    public LimitExceededException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
