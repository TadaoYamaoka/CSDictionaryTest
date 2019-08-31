using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CSDictionaryTest
{
    class Program
    {
        static void Main(string[] args)
        {
            var random = new Random();

            const int MAX_NUM = 5000000;
            var keySet = new HashSet<int>();
            var keys = new List<int>(MAX_NUM);
            do
            {
                int i = random.Next();
                if (!keySet.Contains(i))
                {
                    keySet.Add(i);
                    keys.Add(i);
                }
            } while (keySet.Count < MAX_NUM);

            var sw = new Stopwatch();

            for (int n = 0; n < 10; ++n)
            {
                var dic = new Dictionary<int, double>();
                sw.Reset(); sw.Start();
                for (int i = 0; i < MAX_NUM; ++i)
                {
                    dic.Add(keys[i], i);
                }
                sw.Stop();
                Console.WriteLine(sw.ElapsedMilliseconds);

                sw.Reset(); sw.Start();
                for (int i = 0; i < MAX_NUM; ++i)
                {
                    double val = 0;
                    dic.TryGetValue(i, out val);
                }
                sw.Stop();
                Console.WriteLine(sw.ElapsedMilliseconds);


                /*var mydic = new MyDictionary1.Dictionary<int, double>();
                sw.Reset(); sw.Start();
                for (int i = 0; i < MAX_NUM; ++i)
                {
                    mydic.Add(keys[i], i);
                }
                sw.Stop();
                Console.WriteLine(sw.ElapsedMilliseconds);

                sw.Reset(); sw.Start();
                for (int i = 0; i < MAX_NUM; ++i)
                {
                    double val = 0;
                    mydic.TryGetValue(i, out val);
                }
                sw.Stop();
                Console.WriteLine(sw.ElapsedMilliseconds);*/


                /*var mydic2 = new MyDictionary2.Dictionary<int, double>();
                sw.Reset(); sw.Start();
                for (int i = 0; i < MAX_NUM; ++i)
                {
                    mydic2.Add(keys[i], i);
                }
                sw.Stop();
                Console.WriteLine(sw.ElapsedMilliseconds);

                sw.Reset(); sw.Start();
                for (int i = 0; i < MAX_NUM; ++i)
                {
                    double val = 0;
                    mydic2.TryGetValue(i, out val);
                }
                sw.Stop();
                Console.WriteLine(sw.ElapsedMilliseconds);*/


                /*var mydic3 = new MyDictionary3.Dictionary();
                sw.Reset(); sw.Start();
                for (int i = 0; i < MAX_NUM; ++i)
                {
                    mydic3.Add(keys[i], i);
                }
                sw.Stop();
                Console.WriteLine(sw.ElapsedMilliseconds);

                sw.Reset(); sw.Start();
                for (int i = 0; i < MAX_NUM; ++i)
                {
                    double val = 0;
                    mydic3.TryGetValue(i, out val);
                }
                sw.Stop();
                Console.WriteLine(sw.ElapsedMilliseconds);*/

                Console.WriteLine("");
            }
        }
    }
}
