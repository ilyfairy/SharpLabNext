/**
 * The repository schema gate treats the JSON Schema format vocabulary as an
 * assertion. Keep the small, standards-based subset used by our schemas in a
 * dependency-free helper so the CLI and focused tests share one definition.
 */

const datePattern = /^(\d{4})-(\d{2})-(\d{2})$/;
const dateTimePattern = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d+)?(Z|[+-]\d{2}:\d{2})$/;
const uriSchemePattern = /^[A-Za-z][A-Za-z0-9+.-]*:/;
const forbiddenUriCharacterPattern = /[\u0000-\u0020\u007f]/;

export const supportedJsonSchemaFormats = Object.freeze(['date', 'date-time', 'uri']);

export function isSupportedJsonSchemaFormat(format) {
  return supportedJsonSchemaFormats.includes(format);
}

export function isValidJsonSchemaFormat(value, format) {
  if (typeof value !== 'string') return false;

  switch (format) {
    case 'date':
      return isValidDateParts(datePattern.exec(value));
    case 'date-time':
      return isValidDateTime(dateTimePattern.exec(value));
    case 'uri':
      return isValidUri(value);
    default:
      return false;
  }
}

function isValidDateTime(match) {
  if (match === null || !isValidDateParts(match)) return false;

  const hour = Number(match[4]);
  const minute = Number(match[5]);
  const second = Number(match[6]);
  if (hour > 23 || minute > 59 || second > 59) return false;

  const zone = match[7];
  if (zone === 'Z') return true;
  const offsetHour = Number(zone.slice(1, 3));
  const offsetMinute = Number(zone.slice(4, 6));
  return offsetHour <= 23 && offsetMinute <= 59;
}

function isValidDateParts(match) {
  if (match === null) return false;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  if (month < 1 || month > 12 || day < 1) return false;

  const leapYear = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  const daysInMonth = [31, leapYear ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][month - 1];
  return day <= daysInMonth;
}

function isValidUri(value) {
  if (value.length === 0 || forbiddenUriCharacterPattern.test(value) ||
      !uriSchemePattern.test(value)) return false;
  try {
    // URL accepts all absolute schemes used by our source locks (including
    // https and urn) while rejecting missing authority/path components.
    new URL(value)
    return true;
  } catch {
    return false;
  }
}
