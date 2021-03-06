﻿// Part of FemtoCraft | Copyright 2012-2013 Matvei Stefarov <me@matvei.org> | See LICENSE.txt
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using JetBrains.Annotations;

namespace Testosterone {
    internal static unsafe class Util {
        [NotNull]
        public static string GenerateSalt() {
            RandomNumberGenerator prng = RandomNumberGenerator.Create();
            StringBuilder sb = new StringBuilder();
            byte[] oneChar = new byte[1];
            while (sb.Length < 32) {
                prng.GetBytes(oneChar);
                if (oneChar[0] >= 48 && oneChar[0] <= 57 ||
                    oneChar[0] >= 65 && oneChar[0] <= 90 ||
                    oneChar[0] >= 97 && oneChar[0] <= 122) {
                    sb.Append((char)oneChar[0]);
                }
            }
            return sb.ToString();
        }


        public static void MemSet([NotNull] this byte[] array, byte value, int startIndex, int length) {
            if (array == null) throw new ArgumentNullException("array");
            if (length < 0 || length > array.Length) {
                throw new ArgumentOutOfRangeException("length");
            }
            if (startIndex < 0 || startIndex + length > array.Length) {
                throw new ArgumentOutOfRangeException("startIndex");
            }

            byte[] rawValue = { value, value, value, value, value, value, value, value };
            Int64 fillValue = BitConverter.ToInt64(rawValue, 0);

            fixed (byte* ptr = &array[startIndex]) {
                Int64* dest = (Int64*)ptr;
                while (length >= 8) {
                    *dest = fillValue;
                    dest++;
                    length -= 8;
                }
                byte* bDest = (byte*)dest;
                for (byte i = 0; i < length; i++) {
                    *bDest = value;
                    bDest++;
                }
            }
        }


        [NotNull]
        public static string JoinToString<T>([NotNull] this IEnumerable<T> items, [NotNull] string separator,
                                             [NotNull] Func<T, string> stringConversionFunction) {
            if (items == null) throw new ArgumentNullException("items");
            if (separator == null) throw new ArgumentNullException("separator");
            if (stringConversionFunction == null) throw new ArgumentNullException("stringConversionFunction");
            StringBuilder sb = new StringBuilder();
            bool first = true;
            foreach (T item in items) {
                if (!first) sb.Append(separator);
                sb.Append(stringConversionFunction(item));
                first = false;
            }
            return sb.ToString();
        }


        [NotNull]
        public static string JoinToString<T>([NotNull] this IEnumerable<T> items, [NotNull] string separator) {
            return JoinToString(items, separator, str => str.ToString());
        }


        public static void MoveOrReplaceFile([NotNull] string source, [NotNull] string destination) {
            if (source == null) throw new ArgumentNullException("source");
            if (destination == null) throw new ArgumentNullException("destination");
            if (File.Exists(destination)) {
                if (Path.GetPathRoot(Path.GetFullPath(source)) == Path.GetPathRoot(Path.GetFullPath(destination))) {
                    string backupFileName = destination + ".bak";
                    File.Replace(source, destination, backupFileName, true);
                    File.Delete(backupFileName);
                } else {
                    File.Copy(source, destination, true);
                }
            } else {
                File.Move(source, destination);
            }
        }


        // Code courtesy of TcKs @ http://stackoverflow.com/a/340638/383361
        public static void Raise<TEventArgs>(this EventHandler<TEventArgs> handler,
                                      object sender, TEventArgs e) where TEventArgs : EventArgs {
            if (null != handler) {
                handler(sender, e);
            }
        }
    }
}