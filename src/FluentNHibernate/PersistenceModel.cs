using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml;
using FluentNHibernate.Conventions;
using FluentNHibernate.Mapping;
using FluentNHibernate.Mapping.Providers;
using FluentNHibernate.MappingModel;
using FluentNHibernate.MappingModel.ClassBased;
using FluentNHibernate.MappingModel.Output;
using FluentNHibernate.Utils;
using FluentNHibernate.Visitors;
using NHibernate.Cfg;

namespace FluentNHibernate
{
    public class PersistenceModel
    {
        protected readonly IList<IMappingProvider> classProviders = new List<IMappingProvider>();
        protected readonly IList<IFilterDefinition> filterDefinitions = new List<IFilterDefinition>();
        protected readonly IList<IIndeterminateSubclassMappingProvider> subclassProviders = new List<IIndeterminateSubclassMappingProvider>();
        protected readonly IList<IExternalComponentMappingProvider> componentProviders = new List<IExternalComponentMappingProvider>();
        private readonly IList<IMappingModelVisitor> visitors = new List<IMappingModelVisitor>();
        public IConventionFinder Conventions { get; private set; }
        public bool MergeMappings { get; set; }
        private IEnumerable<HibernateMapping> compiledMappings;

        public PersistenceModel(IConventionFinder conventionFinder)
        {
            Conventions = conventionFinder;

            visitors.Add(new ComponentReferenceResolutionVisitor(componentProviders));
            visitors.Add(new ComponentColumnPrefixVisitor());
            visitors.Add(new SeparateSubclassVisitor(subclassProviders));
            visitors.Add(new BiDirectionalManyToManyPairingVisitor());
            visitors.Add(new ManyToManyTableNameVisitor());
            visitors.Add(new ConventionVisitor(Conventions));
        }

        public PersistenceModel()
            : this(new DefaultConventionFinder())
        {}

        protected void AddMappingsFromThisAssembly()
        {
            var assembly = FindTheCallingAssembly();
            AddMappingsFromAssembly(assembly);
        }

        public void AddMappingsFromAssembly(Assembly assembly)
        {
            AddMappingsFromSource(new AssemblyTypeSource(assembly));
        }

        public void AddMappingsFromSource(ITypeSource source)
        {
            source.GetTypes()
                .Where(x => IsMappingOf<IMappingProvider>(x) ||
                            IsMappingOf<IIndeterminateSubclassMappingProvider>(x) ||
                            IsMappingOf<IExternalComponentMappingProvider>(x) ||
                            IsMappingOf<IFilterDefinition>(x))
                .Each(Add);
        }

        private static Assembly FindTheCallingAssembly()
        {
            StackTrace trace = new StackTrace(Thread.CurrentThread, false);

            Assembly thisAssembly = Assembly.GetExecutingAssembly();
            Assembly callingAssembly = null;
            for (int i = 0; i < trace.FrameCount; i++)
            {
                StackFrame frame = trace.GetFrame(i);
                Assembly assembly = frame.GetMethod().DeclaringType.Assembly;
                if (assembly != thisAssembly)
                {
                    callingAssembly = assembly;
                    break;
                }
            }
            return callingAssembly;
        }

        public void Add(IMappingProvider provider)
        {
            classProviders.Add(provider);
        }

        public void Add(IIndeterminateSubclassMappingProvider provider)
        {
            subclassProviders.Add(provider);
        }

        public void Add(IFilterDefinition definition)
        {
            filterDefinitions.Add(definition);
        }

        public void Add(IExternalComponentMappingProvider provider)
        {
            componentProviders.Add(provider);
        }

        public void Add(Type type)
        {
            var mapping = type.InstantiateUsingParameterlessConstructor();

            if (mapping is IMappingProvider)
                Add((IMappingProvider)mapping);
            else if (mapping is IIndeterminateSubclassMappingProvider)
                Add((IIndeterminateSubclassMappingProvider)mapping);
            else if (mapping is IFilterDefinition)
                Add((IFilterDefinition)mapping);
            else if (mapping is IExternalComponentMappingProvider)
                Add((IExternalComponentMappingProvider)mapping);
            else
                throw new InvalidOperationException("Unsupported mapping type '" + type.FullName + "'");
        }

        private bool IsMappingOf<T>(Type type)
        {
            return !type.IsGenericType && typeof(T).IsAssignableFrom(type);
        }

        public IEnumerable<HibernateMapping> BuildMappings()
        {
            var hbms = new List<HibernateMapping>();

            if (MergeMappings)
                BuildSingleMapping(hbms.Add);
            else
                BuildSeparateMappings(hbms.Add);

            ApplyVisitors(hbms);

            return hbms;
        }

        private void BuildSeparateMappings(Action<HibernateMapping> add)
        {
            foreach (var classMap in classProviders)
            {
                var hbm = classMap.GetHibernateMapping();

                hbm.AddClass(classMap.GetClassMapping());

                add(hbm);
            }
            foreach (var filterDefinition in filterDefinitions)
            {
                var hbm = filterDefinition.GetHibernateMapping();
                hbm.AddFilter(filterDefinition.GetFilterMapping());
                add(hbm);
            }
        }

        private void BuildSingleMapping(Action<HibernateMapping> add)
        {
            var hbm = new HibernateMapping();

            foreach (var classMap in classProviders)
            {
                hbm.AddClass(classMap.GetClassMapping());
            }
            foreach (var filterDefinition in filterDefinitions)
            {
                hbm.AddFilter(filterDefinition.GetFilterMapping());
            }

            if (hbm.Classes.Count() > 0)
                add(hbm);
        }

        private void ApplyVisitors(IEnumerable<HibernateMapping> mappings)
        {
            foreach (var visitor in visitors)
                foreach (var mapping in mappings)
                    mapping.AcceptVisitor(visitor);
        }

        private void EnsureMappingsBuilt()
        {
            if (compiledMappings != null) return;

            compiledMappings = BuildMappings();
        }

        protected virtual string GetMappingFileName()
        {
            return "FluentMappings.hbm.xml";
        }

        public void WriteMappingsTo(string folder)
        {
            EnsureMappingsBuilt();

            foreach (var mapping in compiledMappings)
            {
                var serializer = new MappingXmlSerializer();
                var document = serializer.Serialize(mapping);
                string filename;
                if (MergeMappings)
                {
                    filename = GetMappingFileName();
                }
                else if (mapping.Classes.Count() > 0)
                {
                    filename = mapping.Classes.First().Type.FullName + ".hbm.xml";
                }
                else
                {
                    filename = "filter-def." + mapping.Filters.First().Name + ".hbm.xml";
                }

                using (var writer = new XmlTextWriter(Path.Combine(folder, filename), Encoding.Default))
                {
                    writer.Formatting = Formatting.Indented;
                    document.WriteTo(writer);
                }    
            }
        }

        public virtual void Configure(Configuration cfg)
        {
            EnsureMappingsBuilt();

            foreach (var mapping in compiledMappings.Where(m => m.Classes.Count() == 0))
            {
                var serializer = new MappingXmlSerializer();
                XmlDocument document = serializer.Serialize(mapping);
                cfg.AddDocument(document);
            }

            foreach (var mapping in compiledMappings.Where(m => m.Classes.Count() > 0))
            {
                var serializer = new MappingXmlSerializer();
                XmlDocument document = serializer.Serialize(mapping);

                if (cfg.GetClassMapping(mapping.Classes.First().Type) == null)
                    cfg.AddDocument(document);
            }
        }

        public bool ContainsMapping(Type type)
        {
            return classProviders.Any(x => x.GetType() == type) ||
                filterDefinitions.Any(x => x.GetType() == type) ||
                subclassProviders.Any(x => x.GetType() == type) ||
                componentProviders.Any(x => x.GetType() == type);
        }
    }

    public interface IMappingProvider
    {
        ClassMapping GetClassMapping();
        // HACK: In place just to keep compatibility until verdict is made
        HibernateMapping GetHibernateMapping();
        IEnumerable<string> GetIgnoredProperties();
    }

    public class PassThroughMappingProvider : IMappingProvider
    {
        private readonly ClassMapping mapping;

        public PassThroughMappingProvider(ClassMapping mapping)
        {
            this.mapping = mapping;
        }

        public ClassMapping GetClassMapping()
        {
            return mapping;
        }

        public HibernateMapping GetHibernateMapping()
        {
            return new HibernateMapping();
        }

        public IEnumerable<string> GetIgnoredProperties()
        {
            return new string[0];
        }
    }
}